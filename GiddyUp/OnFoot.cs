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

    // Auto-mount: why we want to mount (null = we don't), right away or once you move, when that started, and the
    // attempts so far (summons the game accepted, and refusals, which are retried sooner).
    private string? _mountReason;
    private MountTiming _mountTiming;
    private int _mountCasts, _mountRefusals;
    private bool _lastTryAccepted;
    private long _mountTriedAt;
    private long _mountWantedAt;
    private nint _targetAtWant;  // your target when the mount was wanted: still having it doesn't cancel the mount

    // After using Sprint/Peloton/a mount, its effect takes a moment to show up (server round trip): don't decide
    // again until it shows, or 2 s.
    private uint _waitStatus;
    private long _waitSince;
    private bool _waiting;

    public OnFoot(Plugin p) => _p = p;

    private Configuration Config => _p.Config;

    internal string? MountReason => _mountReason;

    /// <summary>
    /// Your target when Giddy Up last summoned a mount, and when: an enemy you target while the mount is being
    /// summoned (a summon can't be cancelled) then counts as a new target, so you get off as soon as it's there.
    /// </summary>
    internal nint TargetAtSummon { get; private set; }
    internal long SummonedAt { get; private set; }
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
    /// Mounts allowed here: the zone allows them, and you're not in a duty (field operations like Eureka, Bozja,
    /// Occult Crescent and the Diadem count as duties but allow mounts: those are fine) or PvP. The zone's game data
    /// is read once per zone (keyed on the zone id itself, so the order of zone-change events doesn't matter).
    /// </summary>
    private static bool MountAllowedHere()
    {
        if (Plugin.ClientState.IsPvP)
            return false;
        var zone = Plugin.ClientState.TerritoryType;
        if (zone != _zoneRead)
        {
            _zoneRead = zone;
            _zoneMount = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(zone, out var t) && t.Mount;
            _zoneField = _zoneMount && t.TerritoryIntendedUse.RowId is 26 or 38 or 41 or 47 or 48 or 61;
        }
        var c = Plugin.Condition;
        if (!_zoneField && (c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95]))
            return false;
        return _zoneMount;
    }

    // TerritoryIntendedUse ids from the game data: 26/38/47 the Diadem, 41 Eureka, 48 Bozja/Zadnor, 61 Occult Crescent.
    private static uint _zoneRead = uint.MaxValue;
    private static bool _zoneMount, _zoneField;

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
                Trigger(value, Config.MountAfterZoneChange, Config.ZoneChangeTiming, "zone change");
                break;
            case ConditionFlag.InCombat:
                Trigger(value, Config.MountAfterCombat, Config.CombatTiming, "after combat");
                break;
            case ConditionFlag.Gathering or ConditionFlag.Fishing:
                Trigger(value, Config.MountAfterGathering, Config.GatheringTiming, "after gathering");
                break;
            case ConditionFlag.Crafting:
                Trigger(value, Config.MountAfterCrafting, Config.CraftingTiming, "after crafting");
                break;
            // Talking to someone or using something (quest objects, switches, chests...): both are game "events"
            // (verified in game with Hats Off: NPC talks and object interactions set these same two states).
            case ConditionFlag.OccupiedInEvent or ConditionFlag.OccupiedInQuestEvent:
                Trigger(value, Config.MountAfterTalking, Config.TalkingTiming, "after talking or interacting");
                break;
        }
    }

    private void Trigger(bool started, bool enabled, MountTiming timing, string reason)
    {
        if (started)
            _mountReason = null; // doing something here: no mount until it ends
        else if (enabled)
            WantMount(reason, timing);
    }

    /// <summary>A Beeline trip started: Beeline decides mounting for the trip.</summary>
    internal void OnTripStarted() => _mountReason = null;

    private void WantMount(string reason, MountTiming timing)
    {
        if (_p.Link.TripActive)
            return; // Beeline decides mounting during its trips
        if (Game.IsMounted || !MountAllowedHere())
            return;
        // Already about to mount right away (e.g. a zone change): an event ending just after (a teleport is one too)
        // doesn't turn that into "when you start moving".
        if (_mountReason != null && _mountTiming == MountTiming.RightAway && timing == MountTiming.WhenMoving)
            return;
        _mountReason = reason;
        _mountTiming = timing;
        _mountCasts = _mountRefusals = 0;
        _mountTriedAt = 0;
        _mountWantedAt = Environment.TickCount64;
        _targetAtWant = (nint)TargetSystem.Instance()->Target;
        Plugin.Info($"Will mount ({reason})" + (timing == MountTiming.WhenMoving ? " when you start moving," : "")
                    + (Config.MountSmart ? " once you're done" : $" {Config.MountDelaySeconds:0.0} s after"));
    }

    /// <summary>
    /// Is it time to summon the mount? Smart (default): as soon as you're done, meaning no cast, no animation lock,
    /// and no action used (a Pictomancer painting motifs, buffs, heals...) for one of your global cooldowns plus
    /// your measured action delay, counted from the end of your last cast. A summon is a cast that would block those
    /// actions, so it
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
            Plugin.Log.Error(e, $"On-foot update failed");
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
                // A re-pressed Sprint may have waited in the game's queue: not a clean sample of your action delay.
                if (!_waitSprint || _sprintRetries == 0)
                    _p.Speeds.LearnActionLag(since);
                if (_waitSprint)
                    _sprintRetries = 0;
                _waiting = false;
            }
            else if (_waitSprint && since >= TickMs / 1000f && _sprintRetries < 10
                     && Game.Ready(ActionType.GeneralAction, Game.Sprint))
            {
                // A Sprint that went through starts its cooldown at once: still ready a tick later means the press
                // was dropped (another action went off at the same moment, e.g. one used by another plugin). Press
                // again right away; during that action's animation the game queues it, so it goes off the moment it can.
                if (_sprintRetries++ == 0)
                    Plugin.Info("Sprint didn't go through (another action at the same moment): again");
                _waiting = false;
            }
            else if (since > 2f)
            {
                _waiting = false;
                _sprintRetries = 0;
            }
            else
                return;
        }

        if (_mountReason == null && !moving)
            return; // standing still, nothing to summon: nothing to decide

        var c = Plugin.Condition;
        if (Game.IsMounted || c[ConditionFlag.InCombat] || Game.Busy() || _p.ActionDismount.ReplayPending)
            return;

        var enemyTargeted = EnemyTargeted();

        // 1. Mount, once it's time (see TimeToMount; right away or once you move, per trigger) and the game allows it
        //    here and now. While a mount is pending, no Sprint/Peloton: mounting ends them, they'd only cost time.
        //    Targeting an enemy cancels it: you're about to fight.
        if (_mountReason != null)
        {
            if (enemyTargeted && (nint)TargetSystem.Instance()->Target != _targetAtWant)
            {
                Plugin.Info($"Not mounting ({_mountReason}): you targeted an enemy");
                _mountReason = null;
            }
            else
            {
                if (!MountAllowedHere()) // checked again: a duty may have started since
                    _mountReason = null;
                else if ((_mountTiming == MountTiming.RightAway || _movingFor >= Config.MoveSeconds)
                         && Game.Ready(ActionType.GeneralAction, Game.MountRoulette) && TimeToMount(now))
                    TryMount(now);
                return;
            }
        }

        // No Sprint/Peloton with an enemy targeted either: you're about to fight (their animation lock would only
        // delay your first attack).
        if (walking || _movingFor < Config.MoveSeconds || enemyTargeted)
            return;

        // 2. Sprint: when it's ready and faster than how you move right now.
        var current = Math.Max(Config.RunSpeed, Math.Max(pelotonLeft > 0 ? Config.PelotonSpeed : 0, jogLeft > 0 ? Config.JogSpeed : 0));
        if (Config.AutoSprint && sprintLeft <= 0 && Config.SprintSpeed > current
            && Game.Ready(ActionType.GeneralAction, Game.Sprint) && !_p.Link.BeelineWillMount()
            && Game.Use(ActionType.GeneralAction, Game.Sprint))
        {
            Plugin.Info($"Sprint ({Config.SprintSpeed:0.0} y/s beats {current:0.0})");
            Wait(Game.SprintStatus, now);
            _waitSprint = true;
            return;
        }

        // 3. Peloton: keep it up. It goes on about 2 s before Sprint ends so it has surely taken over by then (no Jog
        //    then), and is renewed just before it runs out ("just before" = your measured action delay, twice, plus
        //    a tick).
        if (!Config.KeepPeloton)
            return;
        var lead = Config.ActionLagSeconds * 2f + TickMs / 1000f * 2f;
        if (pelotonLeft > lead)
            return;
        if (sprintLeft > lead + 1.5f && Config.SprintSpeed >= Config.PelotonSpeed)
            return;
        // On a Beeline trip where Beeline uses Sprint (yours is off): a Sprint it's about to use, or just used (its
        // effect takes a moment to show up), would end up under a Peloton put on now.
        if (sprintLeft <= 0 && !Config.AutoSprint && _p.Link.TripActive && SprintComing(now))
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

    private long _sprintReadySince, _sprintGoneSince;

    /// <summary>
    /// Someone else (Beeline) may be about to use Sprint, or just did: ready for under 3 s (it'd use it by then), or
    /// gone on cooldown under 2 s ago (its effect not showing yet).
    /// </summary>
    private bool SprintComing(long now)
    {
        if (Game.Ready(ActionType.GeneralAction, Game.Sprint))
        {
            _sprintGoneSince = 0;
            if (_sprintReadySince == 0) _sprintReadySince = now;
            return now - _sprintReadySince < 3000;
        }
        _sprintReadySince = 0;
        if (_sprintGoneSince == 0) _sprintGoneSince = now;
        return now - _sprintGoneSince < 2000;
    }

    /// <summary>Your target is something you could attack (as the game decides: auto-attack would work on it).</summary>
    private static bool EnemyTargeted()
    {
        var t = TargetSystem.Instance()->Target;
        return t != null && t->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc
               && !t->IsDead() && ActionManager.CanUseActionOnTarget(7, t);
    }

    private bool _waitSprint; // the wait is for a Sprint press (a dropped one shows: Sprint stays ready)
    private int _sprintRetries; // re-presses of a dropped Sprint, until one goes through

    private void Wait(uint status, long now)
    {
        _waitSprint = false;
        _waiting = true;
        _waitStatus = status;
        _waitSince = now;
    }

    private void TryMount(long now)
    {
        // A summon the game accepted shows as "mounting" (Busy) and takes a moment: try again only if nothing happened
        // for a few seconds (3 summons). A refusal (the game not ready yet, e.g. right after loading in) is retried
        // half a second later, up to 10 times.
        if (_mountTriedAt != 0 && now - _mountTriedAt < (_lastTryAccepted ? 3000 : 500))
            return;
        if (_mountCasts >= 3 || _mountRefusals >= 10)
        {
            Plugin.Info($"Gave up mounting ({_mountReason}): {_mountCasts} summons, {_mountRefusals} refusals");
            _mountReason = null;
            _p.Refresh();
            return;
        }
        _mountTriedAt = now;
        TargetAtSummon = (nint)TargetSystem.Instance()->Target;
        SummonedAt = now;
        bool ok;
        if (Config.MountKind == MountChoice.Specific && Config.MountId != 0 && PlayerState.Instance()->IsMountUnlocked(Config.MountId)
            && Game.Ready(ActionType.Mount, Config.MountId))
            ok = Game.Use(ActionType.Mount, Config.MountId);
        else
            ok = Game.Use(ActionType.GeneralAction, Game.MountRoulette);
        _lastTryAccepted = ok;
        if (ok)
        {
            _mountCasts++;
            Plugin.Info($"Mounting ({_mountReason}, {(now - _mountWantedAt) / 1000f:0.0} s after)");
            Wait(0, now);
        }
        else if (_mountRefusals++ == 0)
            Plugin.Info($"Mount not possible yet ({_mountReason}): retrying");
    }
}
