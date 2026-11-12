using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace GiddyUp;

/// <summary>
/// While you're on a mount: dismount for an enemy you target (as soon as you target it, or once it's within your
/// attack range), and learn how long landing and getting off take (from any dismount Giddy Up makes).
///
/// Flying, Dismount works in two steps in the game: the first press lands you, a second one on the ground gets you
/// off. Giddy Up presses the second one the moment you touch down (for any dismount it started).
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
    private Vector3 _stillPos;       // where you were at the last stillness check (flying)
    private long _stillAt;
    private bool _still = true;
    private bool _toldStill;         // "hold still to land" shown (or the Beeline trip stopped) for this enemy

    // A dismount Giddy Up started (for an enemy or an action): when, from what height, and how it went.
    private long _dismountAt;        // first press (0 = none in progress)
    private bool _dismountInFlight;  // that press was in the air: a landing follows
    private float _dismountHeight;   // height above the ground then
    private long _touchdownAt;       // landed (still on the mount)
    private long _secondPressAt;     // the "get off" press after landing
    private long _lastPressAt;       // last press of any kind (for the 1.5 s between presses)
    private int _presses;            // presses for this target (a cap, so nothing spams)
    private int _landingPresses;     // presses before touching down (more than one = the landing stalled)

    private float _height = float.NaN; // above the ground right below you (NaN = none found), refreshed 4x/s while needed
    private long _heightAt;

    public Mounted(Plugin p) => _p = p;

    private Configuration Config => _p.Config;

    // For the settings header (formatted there, only while the window is open).
    internal enum State { None, Approaching, NoSight, WaitingStill }
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
        // Landed after a dismount Giddy Up started in the air: press again to get off (the game's second step).
        if (flag == ConditionFlag.InFlight && !value && _dismountInFlight)
        {
            _touchdownAt = now;
            if (Plugin.Condition[ConditionFlag.Mounted] && Game.Use(ActionType.GeneralAction, Game.Dismount))
            {
                _secondPressAt = now;
                _lastPressAt = now;
            }
        }
        if (flag == ConditionFlag.Mounted && !value)
        {
            var took = (now - _dismountAt) / 1000f;
            if (_dismountInFlight && _touchdownAt != 0 && !float.IsNaN(_dismountHeight) && _landingPresses == 1)
                _p.Speeds.LearnLanding(_dismountHeight, (_touchdownAt - _dismountAt) / 1000f,
                                       (now - (_secondPressAt != 0 ? _secondPressAt : _touchdownAt)) / 1000f);
            else if (!_dismountInFlight)
                _p.Speeds.LearnLanding(0f, 0f, took); // on the ground: just the get-off part
            Plugin.Info($"Off the mount after {took:0.00} s{(_dismountInFlight ? $" (landed from {_dismountHeight:0} y)" : "")}");
            _dismountAt = 0;
            _touchdownAt = 0;
            _secondPressAt = 0;
        }
    }

    /// <summary>
    /// Giddy Up pressed Dismount (here or for an action). The first press of a dismount records when and from what
    /// height (the landing teaches the landing timings, and the touchdown triggers the second press); later presses of
    /// the same dismount don't reset that.
    /// </summary>
    internal void NoteDismount(long now)
    {
        _lastPressAt = now;
        if (_touchdownAt == 0)
            _landingPresses++;
        if (_dismountAt != 0)
            return;
        _landingPresses = 1;
        _dismountInFlight = Plugin.Condition[ConditionFlag.InFlight];
        _dismountHeight = _dismountInFlight ? HeightAboveLanding() : 0f;
        _dismountAt = now;
        _touchdownAt = 0;
        _secondPressAt = 0;
    }

    private void Start()
    {
        if (_watching) return;
        _watching = true;
        // An enemy already targeted when you mounted doesn't count (you chose to mount anyway)... unless Giddy Up summoned
        // this mount and you targeted the enemy while it was coming: then it's a new target, handled on the first frame.
        var summonedRecently = Environment.TickCount64 - _p.OnFoot.SummonedAt < 10_000;
        _lastTarget = summonedRecently ? _p.OnFoot.TargetAtSummon : (nint)TargetSystem.Instance()->Target;
        _enemy = null;
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
            Plugin.Log.Error(e, "Mounted update failed");
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
        var player = Game.Player;
        if (player == null)
            return;
        var flying = Plugin.Condition[ConditionFlag.InFlight];

        if (Config.EnemyMode == EnemyDismount.WhenTargeted)
        {
            if (!WaitStill(player, now, flying))
                DoDismount(now, 0f, 0f, flying);
            return;
        }

        // Within range? Measured flat (where you'll stand once landed vs. the enemy, ignoring height: nothing can be
        // used from up in the air anyway), edge to edge with both hitboxes.
        var pos = player->Position;
        var gap = Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(_enemy->Position.X, _enemy->Position.Z))
                  - player->HitboxRadius - _enemy->HitboxRadius;
        var dt = (now - _lastFrameAt) / 1000f;
        _lastFrameAt = now;
        if (!float.IsNaN(_lastGap) && dt is > 0 and < 0.5f)
            _closing += ((_lastGap - gap) / dt - _closing) * Math.Min(1f, dt * 4f); // ~0.25 s smoothing
        _lastGap = gap;

        // Flying: start early by the distance you'll still cover while landing (landing time from your height, with
        // your measured descent speed and get-off time, x how fast you're closing in).
        var lead = 0f;
        if (flying)
        {
            RefreshHeight(now);
            lead = _p.Speeds.LandingSeconds(float.IsNaN(_height) ? 0f : _height) * Math.Max(0f, _closing);
        }
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
        if (WaitStill(player, now, flying))
            return;
        DoDismount(now, range, lead, flying);
    }

    /// <summary>
    /// Time to dismount, but you're moving: flying, always wait (moving stops the landing); riding, if that option is
    /// on. Flying, Giddy Up tells you once per enemy ("hold still to land") and stops a Beeline trip, which is what
    /// moves you then. True = wait.
    /// </summary>
    private bool WaitStill(FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara* player, long now, bool flying)
    {
        if (!(flying || Config.RideDismountWhenStill) || IsStill(player, now))
            return false;
        Now = State.WaitingStill;
        if (flying && !_toldStill)
        {
            _toldStill = true;
            if (_p.Link.TripActive)
            {
                Plugin.Info("Enemy in range: stopping the Beeline trip so you can land");
                _p.Link.DismountedForCombat(); // the trip stops, and with it the movement
            }
            else if (Config.HoldStillNotice)
                Plugin.Toast.ShowNormal("Giddy Up: hold still to land");
        }
        return true;
    }

    /// <summary>Are you holding still? (Moved less than a fraction of your own running speed over 0.25 s.)</summary>
    private bool IsStill(FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara* player, long now)
    {
        if (now - _stillAt < 250)
            return _still;
        var pos = player->Position;
        var moved = Vector3.Distance(pos, _stillPos);
        var seconds = (now - _stillAt) / 1000f;
        _still = _stillAt != 0 && seconds < 1f && moved / seconds < Config.RunSpeed * 0.25f;
        _stillPos = pos;
        _stillAt = now;
        return _still;
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
        _presses = 0;
        _toldStill = false;
        Plugin.Info($"Enemy targeted; attack range {_p.Range.Yalms:0} y ({_p.Range.ActionName})");
    }

    /// <summary>
    /// Press Dismount. Not again while a landing is going on (the touchdown triggers the second press), unless it
    /// stalled (you moved, which stops it): then after its expected time. On the ground, again after 1.5 s if nothing
    /// happened. At most 10 presses per target.
    /// </summary>
    private void DoDismount(long now, float range, float lead, bool flying)
    {
        if (Plugin.Condition[ConditionFlag.RidingPillion] || _presses >= 10)
            return;
        if (_lastPressAt != 0 && now - _lastPressAt < 1500)
            return;
        if (flying && _dismountAt != 0 && _dismountInFlight)
        {
            var expected = _p.Speeds.LandingSeconds(float.IsNaN(_dismountHeight) ? 0f : _dismountHeight) * 1000;
            if (now - _lastPressAt < expected + 1000)
                return; // still landing
        }
        if (flying)
            RefreshHeight(now, force: true);
        if (!Game.Use(ActionType.GeneralAction, Game.Dismount))
            return;
        _presses++;
        NoteDismount(now);
        if (range <= 0) Plugin.Info("Dismount: you targeted an enemy");
        else Plugin.Info($"Dismount: enemy within {range:0} y{(lead > 0.5f ? $" (+{lead:0} y to land)" : "")}");
        _p.Link.DismountedForCombat();
    }

    /// <summary>Height above the ground below you, at most 4 times a second (only flying toward an enemy).</summary>
    private void RefreshHeight(long now, bool force = false)
    {
        if (!force && now - _heightAt < 250)
            return;
        _heightAt = now;
        _height = HeightAboveLanding();
    }

    /// <summary>
    /// How high you are above the ground right below you: a collision ray cast straight down from your position (the
    /// game's own terrain and buildings). NaN if it hits nothing.
    /// </summary>
    internal static float HeightAboveLanding()
    {
        var player = Game.Player;
        if (player == null)
            return float.NaN;
        var pos = player->Position;
        if (!BGCollisionModule.RaycastMaterialFilter(pos, new Vector3(0, -1, 0), out var hit, 1000f))
            return float.NaN;
        return Math.Max(0f, pos.Y - hit.Point.Y);
    }
}
