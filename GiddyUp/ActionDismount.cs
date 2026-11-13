using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using UseActionMode = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseActionMode;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace GiddyUp;

/// <summary>
/// Using an action on a mount (one the game won't let you use mounted) gets you off the mount, then the action goes
/// off by itself once you're on your feet (after landing if you were flying).
///
/// Hooks the game's "use action" function: costs nothing until you press an action. A press that wouldn't work
/// anyway (target out of range or out of sight, on cooldown) leaves you on your mount.
/// </summary>
internal sealed unsafe class ActionDismount : IDisposable
{
    private readonly Plugin _p;
    private readonly Hook<ActionManager.Delegates.UseAction>? _hook;
    // The lower-level "use at a location" the game's own use goes through, and that some plugins call directly:
    // watched too, so their actions count (busy after a fight) and their Peloton can be held back like any other.
    private readonly Hook<ActionManager.Delegates.UseActionLocation>? _locHook;

    private bool _queued;
    private ActionType _type;
    private uint _id, _extra, _combo;
    private ulong _target;
    private UseActionMode _mode;
    private long _queuedAt;
    private float _timeout;
    private bool _replaying;
    private long _dismountSentAt, _dismountExpectedMs;

    internal bool HookWorks => _hook != null;
    /// <summary>An action is waiting to be used once you are off the mount (on-foot features hold back meanwhile).</summary>
    internal bool ReplayPending => _queued;

    /// <summary>
    /// When you last used an action or item, or a cast of yours ended (so a long cast counts from its end); Giddy
    /// Up's own Sprint/Peloton/mount don't count.
    /// Auto-mount waits until you've been idle for a GCD, so it doesn't cut into after-fight actions (one write per
    /// action, nothing else).
    /// </summary>
    internal long LastActionAt { get; private set; }

    private long _pelotonHeldLogAt;
    private string? _pelotonHeldWhy;

    public ActionDismount(Plugin p)
    {
        _p = p;
        try
        {
            _hook = Plugin.Hooks.HookFromAddress<ActionManager.Delegates.UseAction>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, Detour);
            _hook.Enable();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"Couldn't hook action use: \"dismount when using an action\" is unavailable");
        }
        try
        {
            _locHook = Plugin.Hooks.HookFromAddress<ActionManager.Delegates.UseActionLocation>(
                (nint)ActionManager.MemberFunctionPointers.UseActionLocation, LocationDetour);
            _locHook.Enable();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Couldn't hook action use at a location: other plugins' actions may go unnoticed");
        }
    }

    public void Dispose()
    {
        StopReplay();
        _hook?.Dispose();
        _locHook?.Dispose();
    }

    private bool LocationDetour(ActionManager* am, ActionType type, uint id, ulong target, Vector3* location, uint extra, byte a7)
    {
        try
        {
            NoteAction(type, id);
            if (HoldPeloton(type, id))
                return false;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Action check failed");
        }
        return _locHook!.Original(am, type, id, target, location, extra, a7);
    }

    /// <summary>Peloton (whoever uses it) is a running buff, not a sign you're still busy: like Sprint, it doesn't delay the mount.</summary>
    private void NoteAction(ActionType type, uint id)
    {
        if (type is ActionType.Action or ActionType.Item && !Game.OwnCall && !_replaying
            && !(type == ActionType.Action && id == Game.PelotonAction))
            LastActionAt = Environment.TickCount64;
    }

    private bool Detour(ActionManager* am, ActionType type, uint id, ulong target, uint extra, UseActionMode mode, uint combo, bool* outAreaTargeted)
    {
        NoteAction(type, id);
        if (HoldPeloton(type, id))
            return false;
        return Dismounting(am, type, id, target, extra, mode, combo, outAreaTargeted);
    }

    /// <summary>
    /// Peloton from elsewhere (another plugin, or you), on foot, when it can only cost time: held back.
    /// </summary>
    private bool HoldPeloton(ActionType type, uint id)
    {
        // - A mount is on its way (after a fight, a teleport, a Beeline leg that mounts...): its animation would push
        //   the summon back by about a second, and mounting ends it anyway.
        // - Out of combat, the enemy Giddy Up just got you off the mount for is targeted and within your attack range:
        //   you're about to attack, and its animation would only delay your first hit.
        if (type == ActionType.Action && id == Game.PelotonAction && !Game.OwnCall
            && !Plugin.Condition[ConditionFlag.Mounted] && !Plugin.Condition[ConditionFlag.RidingPillion])
        {
            var why = _p.OnFoot.MountComing ? "a mount is on its way (mounting would end it)"
                    : !Plugin.Condition[ConditionFlag.InCombat] && _p.OnFoot.AboutToFight() ? "you're about to fight (enemy in range)"
                    : null;
            if (why != null)
            {
                var at = Environment.TickCount64;
                if (at - _pelotonHeldLogAt > 10_000 || why != _pelotonHeldWhy)
                {
                    _pelotonHeldLogAt = at;
                    _pelotonHeldWhy = why;
                    Plugin.Info($"Peloton held back: {why}");
                }
                return true;
            }
        }
        return false;
    }

    private bool Dismounting(ActionManager* am, ActionType type, uint id, ulong target, uint extra, UseActionMode mode, uint combo, bool* outAreaTargeted)
    {
        try
        {
            // Off the mount and another action came first: it wins, no replay on top of it.
            if (_queued && !_replaying && type == ActionType.Action && !Plugin.Condition[ConditionFlag.Mounted])
            {
                Plugin.Info($"{Game.ActionName(id)} came first after dismounting: not using {Game.ActionName(_id)} too");
                StopReplay();
            }

            if (!_replaying && _p.Config.DismountOnAction && Plugin.Condition[ConditionFlag.Mounted]
                && !Plugin.Condition[ConditionFlag.RidingPillion] && NeedsDismount(am, type, id, target))
            {
                var now = Environment.TickCount64;
                // Already getting off (pressed again while landing): don't press Dismount again; with the replay on,
                // this press replaces the waiting action.
                if (now - _dismountSentAt < _dismountExpectedMs)
                {
                    if (_queued)
                        Queue(type, id, target, extra, mode, combo, _timeout);
                    return false;
                }
                // Dismount instead of the action (the original function directly, so this doesn't come back here).
                if (_hook!.Original(am, ActionType.GeneralAction, Game.Dismount, 0xE0000000, 0, UseActionMode.None, 0, null))
                {
                    // Give up if still mounted after twice the expected time (landing from this height, if flying).
                    var flying = Plugin.Condition[ConditionFlag.InFlight];
                    var height = flying ? Mounted.HeightAboveLanding() : 0f;
                    var expected = flying ? _p.Speeds.LandingSeconds(float.IsNaN(height) ? 0f : height) : _p.Config.GetOffSeconds;
                    _dismountSentAt = now;
                    _p.Mounted.NoteDismount(now); // its landing teaches the landing timings
                    _p.Link.DismountedForCombat();
                    _dismountExpectedMs = (long)((expected * 2f + 1f) * 1000);
                    if (_p.Config.ReplayAction)
                        Queue(type, id, target, extra, mode, combo, expected * 2f + 1f);
                    Plugin.Info($"Dismount for {Game.ActionName(id)}{(flying ? " (landing first)" : "")}"
                              + (_p.Config.ReplayAction ? "" : " (not using it afterwards: option off)"));
                }
                return false;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"Action check failed");
        }
        return _hook!.Original(am, type, id, target, extra, mode, combo, outAreaTargeted);
    }

    /// <summary>Would this action work if you weren't on a mount? (Only then is getting off worth it.)</summary>
    private static bool NeedsDismount(ActionManager* am, ActionType type, uint id, ulong targetId)
    {
        if (type != ActionType.Action)
            return false; // general actions (Sprint, Dismount...), mounts, items, emotes...
        var adjusted = am->GetAdjustedActionId(id);
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(adjusted, out var row))
            return false;
        if (row.CanUseWhileMounted || row.ActionCategory.RowId == 12 /* mount actions */ || adjusted is 5 or 6 /* Teleport, Return */
            || row.IsPvP)
            return false;
        if (am->GetActionStatus(type, id, targetId, false, false) == 0)
            return false; // usable as it is
        if (!am->IsActionOffCooldown(type, id))
            return false;

        // Targeted at someone: must be a valid target, in range and in sight.
        if (row.Range != 0 && !row.TargetArea && !row.CanTargetSelf)
        {
            var target = Find(targetId);
            if (target == null)
                return false;
            if (!ActionManager.CanUseActionOnTarget(adjusted, target))
                return false;
            var player = Game.Player;
            if (player != null && ActionManager.GetActionInRangeOrLoS(adjusted, (GameObject*)player, target) is 562 or 566)
                return false;
        }
        return true;
    }

    /// <summary>The action's target; "no id" (hotbar presses) means your current target, as the game does it.</summary>
    private static GameObject* Find(ulong id)
    {
        if (id == 0xE0000000 || id == 0)
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            var t = ts->GetHardTarget();
            return t != null ? t : ts->GetSoftTarget();
        }
        var obj = Plugin.ObjectTable.SearchById(id);
        return obj == null ? null : (GameObject*)obj.Address;
    }

    // ---------------- Replay once off the mount ----------------

    private void Queue(ActionType type, uint id, ulong target, uint extra, UseActionMode mode, uint combo, float timeout)
    {
        (_type, _id, _target, _extra, _mode, _combo) = (type, id, target, extra, mode, combo);
        _queuedAt = Environment.TickCount64;
        _timeout = timeout;
        if (_queued) return;
        _queued = true;
        _offAt = 0;
        Plugin.Framework.Update += OnUpdate;
    }

    private void StopReplay()
    {
        if (!_queued) return;
        _queued = false;
        Plugin.Framework.Update -= OnUpdate;
    }

    internal void Reset() => StopReplay();

    /// <summary>A cast just ended: "idle" counts from here, not from when it started.</summary>
    internal void OnCondition(Dalamud.Game.ClientState.Conditions.ConditionFlag flag, bool value)
    {
        if (!value && flag is ConditionFlag.Casting or ConditionFlag.Casting87)
            LastActionAt = Environment.TickCount64;
    }

    private long _offAt, _triedAt; // when you were first seen off the mount; last replay attempt

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        var c = Plugin.Condition;
        var stillOn = c[ConditionFlag.Mounted] || c[ConditionFlag.Jumping] || c[ConditionFlag.Jumping61] || c[ConditionFlag.MountOrOrnamentTransition];
        if (stillOn)
        {
            _offAt = 0;
            if ((now - _queuedAt) / 1000f > _timeout)
            {
                Plugin.Info($"Gave up on {Game.ActionName(_id)}: still on the mount after {_timeout:0.0} s");
                StopReplay();
            }
            return;
        }
        // Off the mount. Right after getting off, the game can still refuse actions for a moment (animation lock):
        // try again every 100 ms, for up to your measured get-off time plus a second.
        if (_offAt == 0)
        {
            _offAt = now;
            _triedAt = 0;
        }
        if (now - _triedAt < 100)
            return;
        _triedAt = now;
        bool ok;
        _replaying = true;
        try
        {
            ok = ActionManager.Instance()->UseAction(_type, _id, _target, _extra, _mode, _combo);
        }
        finally
        {
            _replaying = false;
        }
        if (ok)
        {
            Plugin.Info($"{Game.ActionName(_id)} used after dismounting");
            StopReplay();
        }
        else if (now - _offAt > (_p.Config.GetOffSeconds + 1f) * 1000)
        {
            Plugin.Info($"{Game.ActionName(_id)} refused by the game after dismounting");
            StopReplay();
        }
    }
}
