using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GiddyUp;

/// <summary>
/// On foot, out of combat: Sprint, Peloton and auto-mount. Runs 10 times a second, and only while you're on foot
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

    // Auto-mount: why we want to mount (null = we don't), and the attempts so far.
    private string? _mountReason;
    private int _mountAttempts;
    private long _mountTriedAt;
    private bool _territoryAllowsMount;

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
        var want = Plugin.ClientState.IsLoggedIn && !Plugin.ClientState.IsPvP
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

    internal void OnTerritory(uint territory)
    {
        _territoryAllowsMount = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territory, out var t) && t.Mount;
        _hasLastPos = false;
    }

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
                if (value) _mountReason = null;
                else if (Config.MountAfterZoneChange) WantMount("zone change");
                break;
            case ConditionFlag.InCombat:
                if (value) _mountReason = null;
                else if (Config.MountAfterCombat) WantMount("after combat");
                break;
            case ConditionFlag.Gathering:
                if (value) _mountReason = null;
                else if (Config.MountAfterGathering) WantMount("after gathering");
                break;
        }
    }

    private void WantMount(string reason)
    {
        var c = Plugin.Condition;
        if (!_territoryAllowsMount || c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95]
            || Plugin.ClientState.IsPvP || Game.IsMounted)
            return;
        _mountReason = reason;
        _mountAttempts = 0;
        Plugin.Info($"Will mount ({reason}){(Config.MountWhen == MountTiming.WhenMoving ? " when you start moving" : "")}");
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

        var sprintLeft  = Game.StatusLeft(player, Game.SprintStatus);
        var pelotonLeft = Game.StatusLeft(player, Game.PelotonStatus);
        var jogLeft     = Game.StatusLeft(player, Game.JogStatus);
        if (sprintLeft > 0)
            _p.Speeds.SeenSprintDuration(sprintLeft);

        // Learn how fast you go in each way of moving.
        if (moving && !walking)
        {
            var mode = sprintLeft > 0 ? MoveMode.Sprint : pelotonLeft > 0 ? MoveMode.Peloton : jogLeft > 0 ? MoveMode.Jog : MoveMode.Run;
            if (_lastMode is { } last && last != mode)
                _p.Speeds.Reset(last);
            _lastMode = mode;
            _p.Speeds.Slice(mode, moved, dt);
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

        var c = Plugin.Condition;
        if (walking || Game.IsMounted || c[ConditionFlag.InCombat] || Game.Busy() || _p.ActionDismount.ReplayPending)
            return;

        var reacted = _movingFor >= Config.MoveSeconds;

        // 1. Mount, if we want to and the game allows it right here, right now.
        var mountReady = _mountReason != null && Game.Ready(ActionType.GeneralAction, Game.MountRoulette);
        if (mountReady && (reacted || Config.MountWhen == MountTiming.RightAway))
        {
            TryMount(now);
            return;
        }
        if (!reacted)
            return;

        // 2. Sprint: when it's ready and faster than how you move right now. Not when a mount is about to be
        //    summoned (mounting ends Sprint and Jog anyway).
        var current = Math.Max(Config.RunSpeed, Math.Max(pelotonLeft > 0 ? Config.PelotonSpeed : 0, jogLeft > 0 ? Config.JogSpeed : 0));
        if (Config.AutoSprint && !mountReady && sprintLeft <= 0 && Config.SprintSpeed > current
            && Game.Ready(ActionType.GeneralAction, Game.Sprint) && Game.Use(ActionType.GeneralAction, Game.Sprint))
        {
            Plugin.Info($"Sprint ({Config.SprintSpeed:0.0} y/s beats {current:0.0})");
            Wait(Game.SprintStatus, now);
            return;
        }

        // 3. Peloton: keep it up. It goes on in Sprint's last moments so it takes over when Sprint ends (no Jog then),
        //    and is renewed just before it runs out. "Last moments" = your measured action delay, twice, plus a tick.
        if (!Config.KeepPeloton || mountReady)
            return;
        var lead = Config.ActionLagSeconds * 2f + TickMs / 1000f * 2f;
        if (pelotonLeft > lead)
            return;
        if (sprintLeft > lead && Config.SprintSpeed >= Config.PelotonSpeed)
            return;
        if (jogLeft > 0 && sprintLeft <= 0 && Config.JogSpeed >= Config.PelotonSpeed)
            return;
        if (Game.Ready(ActionType.Action, Game.PelotonAction) && Game.Use(ActionType.Action, Game.PelotonAction))
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
        var am = ActionManager.Instance();
        bool ok;
        if (Config.MountKind == MountChoice.Specific && Config.MountId != 0 && PlayerState.Instance()->IsMountUnlocked(Config.MountId)
            && am->GetActionStatus(ActionType.Mount, Config.MountId) == 0)
            ok = am->UseAction(ActionType.Mount, Config.MountId);
        else
            ok = am->UseAction(ActionType.GeneralAction, Game.MountRoulette);
        Plugin.Info(ok ? $"Mounting ({_mountReason})" : $"Mount refused ({_mountReason}), try {_mountAttempts}");
        if (ok)
            Wait(0, now);
    }
}
