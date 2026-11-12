using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GiddyUp;

/// <summary>
/// On foot, out of combat: auto-mount, Sprint and Peloton. Runs 10 times a second, and only while you're on foot
/// out of combat with one of these features on; otherwise it is unsubscribed and costs nothing.
/// </summary>
internal sealed unsafe class OnFoot : IDisposable
{
    private const long TickMs = 100;
    private readonly Plugin _p;
    private bool _watching;
    private long _nextTick, _lastTickAt;
    private Vector3 _lastPos;
    private bool _hasLastPos;
    private float _movingFor;
    private MoveMode? _lastMode;

    // Auto-mount: why we want to mount (null = we don't), when that started, and the attempts so far.
    private string? _mountReason;
    private int _mountAttempts;
    private long _mountTriedAt;
    private long _mountWantedAt;

    // After using Sprint/Peloton/a mount, its effect takes a moment to show up (server round trip): don't decide
    // again until it shows, or 2 s.
    private uint _waitStatus;
    private long _waitSince;
    private bool _waiting;

    public OnFoot(Plugin p) => _p = p;

    private Configuration Config => _p.Config;

    internal string? MountReason => _mountReason;
    internal bool Watching => _watching;

    public void Dispose() => Stop();

    // ---------------- Arming ----------------

    internal void Refresh()
    {
        var c = Plugin.Condition;
        var want = _p.LoggedIn && !Plugin.ClientState.IsPvP
                   && !Game.IsMounted && !c[ConditionFlag.InCombat] && !c[ConditionFlag.BetweenAreas]
                   && (Config.AutoSprint || Config.KeepPeloton || _mountReason != null);
        if (want) Start();
        else Stop();
    }

    private void Start()
    {
        if (_watching) return;
        _watching = true;
        _hasLastPos = false;
        _movingFor = 0;
        Plugin.Framework.Update += OnUpdate;
    }

    private void Stop()
    {
        if (!_watching) return;
        _watching = false;
        if (_lastMode is { } m) _p.Speeds.Reset(m);
        _lastMode = null;
        Plugin.Framework.Update -= OnUpdate;
    }

    internal void OnTerritory(uint territory) => _hasLastPos = false;

    internal void Reset()
    {
        _mountReason = null;
        _waiting = false;
        _hasLastPos = false;
        Stop();
    }

    /// <summary>
    /// Mounts allowed here: the zone allows them (read at the moment, not cached, so the order of zone-change events
    /// doesn't matter), and you're not in a duty or PvP.
    /// </summary>
    private static bool MountAllowedHere()
    {
        var c = Plugin.Condition;
        if (c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95] || Plugin.ClientState.IsPvP)
            return false;
        return Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(Plugin.ClientState.TerritoryType, out var t) && t.Mount;
    }

    /// <summary>
    /// What ends (a zone change, a fight, gathering, crafting, a conversation) makes Giddy Up want to mount; starting
    /// one of those again cancels it.
    /// </summary>
    internal void OnCondition(ConditionFlag flag, bool value)
    {
        switch (flag)
        {
            case ConditionFlag.Mounted or ConditionFlag.RidingPillion when value:
                if (_mountReason != null)
                    Plugin.Info($"Mounted ({_mountReason} done)");
                _mountReason = null;
                break;
            case ConditionFlag.BetweenAreas:
                Trigger(value, Config.MountAfterZoneChange, "zone change");
                break;
            case ConditionFlag.InCombat:
                Trigger(value, Config.MountAfterCombat, "after combat");
                break;
            case ConditionFlag.Gathering or ConditionFlag.Fishing:
                Trigger(value, Config.MountAfterGathering, "after gathering");
                break;
            case ConditionFlag.Crafting:
                Trigger(value, Config.MountAfterCrafting, "after crafting");
                break;
            // Talking to someone, or using something (quest objects, switches, chests...): the game marks these as
            // "occupied" in one of several ways. Logged, so an interaction that doesn't trigger can be identified.
            case ConditionFlag.OccupiedInEvent or ConditionFlag.OccupiedInQuestEvent or ConditionFlag.Occupied
                 or ConditionFlag.Occupied30 or ConditionFlag.Occupied33 or ConditionFlag.Occupied38 or ConditionFlag.Occupied39:
                Plugin.Info($"{flag} {(value ? "started" : "ended")}");
                Trigger(value, Config.MountAfterTalking, "after talking or interacting");
                break;
        }
    }

    private void Trigger(bool started, bool enabled, string reason)
    {
        if (started)
            _mountReason = null; // doing something here: no mount until it ends
        else if (enabled)
            WantMount(reason);
    }

    /// <summary>A Beeline trip started: Beeline decides mounting for the trip.</summary>
    internal void OnTripStarted() => _mountReason = null;

    private void WantMount(string reason)
    {
        if (_p.Link.TripActive)
            return; // Beeline decides mounting during its trips
        if (Game.IsMounted || !MountAllowedHere())
            return;
        _mountReason = reason;
        _mountAttempts = 0;
        _mountWantedAt = Environment.TickCount64;
        Plugin.Info($"Will mount ({reason})" + (Config.MountSmart ? " once you're done" : $" in {Config.MountDelaySeconds:0.0} s"));
    }

    /// <summary>
    /// Is it time to summon the mount? Smart (default): as soon as you're done, meaning no cast, no animation lock,
    /// and no action used (by you or a rotation plugin: a Pictomancer painting motifs, buffs, heals...) for one of
    /// your global cooldowns plus your measured action delay. A summon is a cast that would block those actions, so it
    /// waits for them. Otherwise: your fixed delay.
    /// </summary>
    private bool TimeToMount(long now)
    {
        if (!Config.MountSmart)
            return now - _mountWantedAt >= Config.MountDelaySeconds * 1000;
        if (ActionManager.Instance()->AnimationLock > 0)
            return false;
        var idleMs = Game.GcdMs(_p.Range.ActionId) + Config.ActionLagSeconds * 1000 + TickMs;
        return now - _p.ActionDismount.LastActionAt >= idleMs;
    }

    // ---------------- Every 100 ms ----------------

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now < _nextTick)
            return;
        _nextTick = now + TickMs;
        try
        {
            Tick(now);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"{Plugin.Tag} On-foot update failed");
            _nextTick = now + 5000;
        }
    }

    private void Tick(long now)
    {
        var player = Game.Player;
        if (player == null)
        {
            _hasLastPos = false;
            return;
        }

        _p.Range.Ensure(); // one compare unless your job or level changed

        // How you're moving (flat distance over the last slice).
        var pos = player->Position;
        var dt = (now - _lastTickAt) / 1000f;
        _lastTickAt = now;
        var moved = _hasLastPos ? Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(_lastPos.X, _lastPos.Z)) : 0f;
        _lastPos = pos;
        var hadPos = _hasLastPos;
        _hasLastPos = true;
        if (!hadPos || dt > 0.5f)
            return;
        var walking = Control.Instance()->IsWalking;
        var moving = moved / dt > Config.RunSpeed * 0.25f; // a fraction of your own learned speed
        _movingFor = moving ? _movingFor + dt : 0f;

        float sprintLeft = 0, pelotonLeft = 0, jogLeft = 0;
        if (moving)
        {
            sprintLeft  = Game.StatusLeft(player, Game.SprintStatus);
            pelotonLeft = Game.StatusLeft(player, Game.PelotonStatus);
            jogLeft     = Game.StatusLeft(player, Game.JogStatus);
            if (sprintLeft > 0)
                _p.Speeds.SeenSprintDuration(sprintLeft);
            // Learn how fast you go in each way of moving.
            if (!walking)
            {
                var mode = sprintLeft > 0 ? MoveMode.Sprint : pelotonLeft > 0 ? MoveMode.Peloton : jogLeft > 0 ? MoveMode.Jog : MoveMode.Run;
                if (_lastMode is { } last && last != mode)
                    _p.Speeds.Reset(last);
                _lastMode = mode;
                _p.Speeds.Slice(mode, moved, dt);
            }
        }

        // Effect of the last thing we used showed up? (also learns that delay)
        if (_waiting)
        {
            var since = (now - _waitSince) / 1000f;
            if (_waitStatus != 0 && Game.StatusLeft(player, _waitStatus) > 0)
            {
                _p.Speeds.LearnActionLag(since);
                _waiting = false;
            }
            else if (since > 2f)
                _waiting = false;
            else
                return;
        }

        if (_mountReason == null && !moving)
            return; // standing still, nothing to summon: nothing to decide

        var c = Plugin.Condition;
        if (Game.IsMounted || c[ConditionFlag.InCombat] || Game.Busy() || _p.ActionDismount.ReplayPending)
            return;

        // 1. Mount, moving or not, once it's time (see TimeToMount) and the game allows it right here, right now.
        //    While a mount is on its way, no Sprint/Peloton (mounting ends them anyway).
        if (_mountReason != null && Game.Ready(ActionType.GeneralAction, Game.MountRoulette))
        {
            if (!MountAllowedHere()) // checked again: a duty may have started since
                _mountReason = null;
            else if (TimeToMount(now))
                TryMount(now);
            return;
        }

        if (walking || _movingFor < Config.MoveSeconds)
            return;

        // 2. Sprint: when it's ready and faster than how you move right now.
        var current = Math.Max(Config.RunSpeed, Math.Max(pelotonLeft > 0 ? Config.PelotonSpeed : 0, jogLeft > 0 ? Config.JogSpeed : 0));
        if (Config.AutoSprint && sprintLeft <= 0 && Config.SprintSpeed > current
            && Game.Ready(ActionType.GeneralAction, Game.Sprint) && !_p.Link.BeelineWillMount()
            && Game.Use(ActionType.GeneralAction, Game.Sprint))
        {
            Plugin.Info($"Sprint ({Config.SprintSpeed:0.0} y/s beats {current:0.0})");
            Wait(Game.SprintStatus, now);
            return;
        }

        // 3. Peloton: keep it up. It goes on in Sprint's last moments so it takes over when Sprint ends (no Jog then),
        //    and is renewed just before it runs out. "Last moments" = your measured action delay, twice, plus a tick.
        if (!Config.KeepPeloton)
            return;
        var lead = Config.ActionLagSeconds * 2f + TickMs / 1000f * 2f;
        if (pelotonLeft > lead)
            return;
        if (sprintLeft > lead && Config.SprintSpeed >= Config.PelotonSpeed)
            return;
        if (jogLeft > 0 && sprintLeft <= 0 && Config.JogSpeed >= Config.PelotonSpeed)
            return;
        if (Game.Ready(ActionType.Action, Game.PelotonAction) && !_p.Link.BeelineWillMount()
            && Game.Use(ActionType.Action, Game.PelotonAction))
        {
            Plugin.Info(pelotonLeft > 0 ? "Peloton renewed" : sprintLeft > 0 ? "Peloton (taking over from Sprint)" : "Peloton");
            Wait(pelotonLeft > 0 ? 0 : Game.PelotonStatus, now);
        }
    }

    private void Wait(uint status, long now)
    {
        _waiting = true;
        _waitStatus = status;
        _waitSince = now;
    }

    private void TryMount(long now)
    {
        // A summon in progress shows as "mounting" (Busy); if nothing happened for a while, try again (3 tries).
        if (_mountAttempts > 0 && now - _mountTriedAt < 3000)
            return;
        if (_mountAttempts >= 3)
        {
            Plugin.Info($"Gave up mounting ({_mountReason}): the game refused 3 times");
            _mountReason = null;
            _p.Refresh();
            return;
        }
        _mountAttempts++;
        _mountTriedAt = now;
        bool ok;
        if (Config.MountKind == MountChoice.Specific && Config.MountId != 0 && PlayerState.Instance()->IsMountUnlocked(Config.MountId)
            && Game.Ready(ActionType.Mount, Config.MountId))
            ok = Game.Use(ActionType.Mount, Config.MountId);
        else
            ok = Game.Use(ActionType.GeneralAction, Game.MountRoulette);
        Plugin.Info(ok ? $"Mounting ({_mountReason}, {(now - _mountWantedAt) / 1000f:0.0} s after)"
                       : $"Mount refused ({_mountReason}), try {_mountAttempts}");
        if (ok)
            Wait(0, now);
    }
}
