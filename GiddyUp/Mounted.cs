using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace GiddyUp;

/// <summary>
/// While you're on a mount: dismount for an enemy you target (as soon as you target it, or once it's within your
/// attack range), and learn how long landing and getting off take (from any dismount Giddy Up makes).
///
/// Subscribed only while mounted with the enemy dismount on. Per frame: one compare of the game's target pointer;
/// only while an enemy is targeted, a distance check (and 4 times a second, a height/line-of-sight check).
/// </summary>
internal sealed unsafe class Mounted : IDisposable
{
    private readonly Plugin _p;
    private bool _watching;
    private nint _lastTarget;
    private GameObject* _enemy;
    private float _lastGap = float.NaN;
    private float _closing;          // how fast you're getting closer to the enemy (y/s, smoothed)
    private long _lastFrameAt;
    private long _losCheckedAt;
    private bool _losBlocked;        // last line-of-sight check said no (kept until the next check)

    private long _dismountAt;        // when Giddy Up pressed Dismount (0 = it didn't)
    private bool _dismountInFlight;
    private float _dismountHeight;   // height above the landing spot when it was pressed
    private long _touchdownAt;       // "in flight" ended (on the ground, still on the mount)
    private int _dismountTries;

    private float _height = float.NaN; // above the spot you'd land on (NaN = can't land here), refreshed 4x/s while needed
    private long _heightAt;

    public Mounted(Plugin p) => _p = p;

    private Configuration Config => _p.Config;

    // For the settings header (formatted there, only while the window is open).
    internal enum State { None, Approaching, NoLanding, NoSight }
    internal State Now { get; private set; }
    internal float Gap { get; private set; }
    internal float DismountAt { get; private set; }

    public void Dispose() => Stop();

    internal void Refresh()
    {
        if (_p.LoggedIn && Config.DismountOnEnemy && Plugin.Condition[ConditionFlag.Mounted]) Start();
        else Stop();
    }

    internal void Reset()
    {
        Stop();
        _dismountAt = 0;
        _touchdownAt = 0;
    }

    internal void OnCondition(ConditionFlag flag, bool value)
    {
        if (_dismountAt == 0)
            return;
        var now = Environment.TickCount64;
        if (flag == ConditionFlag.InFlight && !value && _dismountInFlight)
            _touchdownAt = now;
        if (flag == ConditionFlag.Mounted && !value)
        {
            var took = (now - _dismountAt) / 1000f;
            if (_dismountInFlight && _touchdownAt != 0 && !float.IsNaN(_dismountHeight))
                _p.Speeds.LearnLanding(_dismountHeight, (_touchdownAt - _dismountAt) / 1000f, (now - _touchdownAt) / 1000f);
            else if (!_dismountInFlight)
                _p.Speeds.LearnLanding(0f, 0f, took); // on the ground: just the get-off part
            Plugin.Info($"Off the mount after {took:0.00} s{(_dismountInFlight ? $" (landed from {_dismountHeight:0} y)" : "")}");
            _dismountAt = 0;
            _touchdownAt = 0;
        }
    }

    /// <summary>
    /// Giddy Up is pressing Dismount (here or for an action): remember when and from what height, so the landing
    /// teaches the landing timings.
    /// </summary>
    internal void NoteDismount(long now)
    {
        _dismountInFlight = Plugin.Condition[ConditionFlag.InFlight];
        _dismountHeight = _dismountInFlight ? HeightAboveLanding() : 0f;
        _dismountAt = now;
        _touchdownAt = 0;
    }

    private void Start()
    {
        if (_watching) return;
        _watching = true;
        // An enemy already targeted when you mounted doesn't count (you chose to mount anyway).
        _lastTarget = (nint)TargetSystem.Instance()->Target;
        _enemy = null;
        _dismountTries = 0;
        Now = State.None;
        Plugin.Framework.Update += OnUpdate;
    }

    private void Stop()
    {
        if (!_watching) return;
        _watching = false;
        _enemy = null;
        Now = State.None;
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            Frame();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"{Plugin.Tag} Mounted update failed");
            Stop();
        }
    }

    private void Frame()
    {
        // Target changed? (raw pointer compare: nothing else happens until it does)
        var raw = (nint)TargetSystem.Instance()->Target;
        if (raw != _lastTarget)
        {
            _lastTarget = raw;
            OnTargetChanged((GameObject*)raw);
        }
        if (_enemy == null)
            return;
        if (_enemy->IsDead())
        {
            _enemy = null;
            Now = State.None;
            return;
        }

        var now = Environment.TickCount64;
        if (Config.EnemyMode == EnemyDismount.WhenTargeted)
        {
            DoDismount(now, 0f, 0f);
            return;
        }

        // Within range? Distance edge to edge (both hitboxes), in 3D.
        var player = Game.Player;
        if (player == null)
            return;
        var from = player->Position;

        // Flying, the character lands before it's off the mount: measure from the spot you'd land on (so hovering
        // right above an enemy counts as close), and start early by the distance you'll still cover while landing:
        // landing time from your height (your measured descent speed and get-off time) x how fast you're closing in.
        var flying = Plugin.Condition[ConditionFlag.InFlight];
        if (flying)
        {
            RefreshHeight(now);
            if (float.IsNaN(_height))
            {
                Now = State.NoLanding;
                _lastGap = float.NaN;
                return;
            }
            from.Y -= _height;
        }
        var gap = Vector3.Distance(from, _enemy->Position) - player->HitboxRadius - _enemy->HitboxRadius;
        var dt = (now - _lastFrameAt) / 1000f;
        _lastFrameAt = now;
        if (!float.IsNaN(_lastGap) && dt is > 0 and < 0.5f)
            _closing += ((_lastGap - gap) / dt - _closing) * Math.Min(1f, dt * 4f); // ~0.25 s smoothing
        _lastGap = gap;
        var lead = flying ? _p.Speeds.LandingSeconds(_height) * Math.Max(0f, _closing) : 0f;
        var range = _p.Range.Yalms;
        Gap = gap;
        DismountAt = range + lead;
        Now = State.Approaching;
        if (gap - lead > range)
            return;

        // In range: make sure you could actually hit it from here (line of sight). Checked 4 times a second; the
        // answer holds in between. (Not while flying: what you see from up there says nothing about the landing spot.)
        if (!flying && _p.Range.ActionId != 0)
        {
            if (now - _losCheckedAt >= 250)
            {
                _losCheckedAt = now;
                _losBlocked = ActionManager.GetActionInRangeOrLoS(_p.Range.ActionId, (GameObject*)player, _enemy) == 562;
            }
            if (_losBlocked)
            {
                Now = State.NoSight;
                return;
            }
        }
        DoDismount(now, range, lead);
    }

    private void OnTargetChanged(GameObject* target)
    {
        _enemy = null;
        Now = State.None;
        _lastGap = float.NaN;
        _closing = 0;
        _losCheckedAt = 0;
        _losBlocked = false;
        _lastFrameAt = Environment.TickCount64;
        if (target == null)
            return;
        // An enemy = something you could auto-attack (action 7), as the game itself decides.
        if (target->ObjectKind != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc
            || !ActionManager.CanUseActionOnTarget(7, target) || target->IsDead())
            return;
        _p.Range.Recheck(); // picks again from the cached list: newly unlocked actions count
        _enemy = target;
        _dismountTries = 0;
        Plugin.Info($"Enemy targeted; attack range {_p.Range.Yalms:0} y ({_p.Range.ActionName})");
    }

    private void DoDismount(long now, float range, float lead)
    {
        if (_dismountAt != 0 && now - _dismountAt < 1500)
            return; // pressed already: landing/dismounting takes a moment
        if (_dismountTries >= 3 || Plugin.Condition[ConditionFlag.RidingPillion])
            return;
        _dismountTries++;
        if (Plugin.Condition[ConditionFlag.InFlight])
            RefreshHeight(now, force: true);
        if (!Game.Use(ActionType.GeneralAction, Game.Dismount))
            return;
        NoteDismount(now);
        _dismountHeight = _height; // just measured
        if (range <= 0) Plugin.Info("Dismount: you targeted an enemy");
        else Plugin.Info($"Dismount: enemy within {range:0} y{(lead > 0.5f ? $" (+{lead:0} y to land)" : "")}");
        _p.Link.DismountedForCombat();
    }

    /// <summary>Height above the landing spot, at most 4 times a second (only flying toward an enemy).</summary>
    private void RefreshHeight(long now, bool force = false)
    {
        if (!force && now - _heightAt < 250)
            return;
        _heightAt = now;
        _height = HeightAboveLanding();
    }

    /// <summary>Height above the spot the game would land you on (its own "can I dismount here" check); NaN if none.</summary>
    internal static float HeightAboveLanding()
    {
        var player = Game.Player;
        var ui = UIState.Instance();
        if (player == null || ui == null)
            return float.NaN;
        FFXIVClientStructs.FFXIV.Common.Math.Vector3 ground;
        if (!ui->GetIsAirDismountable(&ground))
            return float.NaN;
        return Math.Max(0f, player->Position.Y - ground.Y);
    }
}
