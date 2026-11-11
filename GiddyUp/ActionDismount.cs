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
            Plugin.Log.Error(e, $"{Plugin.Tag} Couldn't hook action use: \"dismount when using an action\" is unavailable");
        }
    }

    public void Dispose()
    {
        StopReplay();
        _hook?.Dispose();
    }

    private bool Detour(ActionManager* am, ActionType type, uint id, ulong target, uint extra, UseActionMode mode, uint combo, bool* outAreaTargeted)
    {
        try
        {
            // Off the mount and another action came first (a rotation plugin, or you): it wins, no replay on top of it.
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
            Plugin.Log.Error(e, $"{Plugin.Tag} Action check failed");
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

    private static GameObject* Find(ulong id)
    {
        if (id == 0xE0000000 || id == 0)
            return null;
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
        Plugin.Framework.Update += OnUpdate;
    }

    private void StopReplay()
    {
        if (!_queued) return;
        _queued = false;
        Plugin.Framework.Update -= OnUpdate;
    }

    internal void OnCondition(ConditionFlag flag, bool value)
    {
        // Nothing to do here: the replay waits on the next frame after "mounted" ends (see OnUpdate).
    }

    private void OnUpdate(IFramework framework)
    {
        if ((Environment.TickCount64 - _queuedAt) / 1000f > _timeout)
        {
            Plugin.Info($"Gave up on {Game.ActionName(_id)}: still on the mount after {_timeout:0.0} s");
            StopReplay();
            return;
        }
        var c = Plugin.Condition;
        if (c[ConditionFlag.Mounted] || c[ConditionFlag.Jumping61] || c[ConditionFlag.MountOrOrnamentTransition])
            return;
        StopReplay();
        _replaying = true;
        try
        {
            var ok = ActionManager.Instance()->UseAction(_type, _id, _target, _extra, _mode, _combo);
            Plugin.Info($"{Game.ActionName(_id)} {(ok ? "used" : "refused by the game")} after dismounting");
        }
        finally
        {
            _replaying = false;
        }
    }
}
