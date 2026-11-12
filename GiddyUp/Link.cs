using Dalamud.Plugin.Ipc;

namespace GiddyUp;

/// <summary>
/// The link with Beeline (map travel plugin by the same author). Both work alone; together:
/// - Beeline says when a trip starts/ends and whether the current leg will mount; during a trip Beeline decides
///   mounting (it knows the route), Giddy Up handles Sprint/Peloton on foot legs and dismounts for enemies.
/// - Beeline's learned speeds are used for every speed it has measured; Giddy Up only learns the others.
/// Event-driven: no polling of the other plugin.
/// </summary>
internal sealed class Link : IDisposable
{
    private readonly Plugin _p;
    private readonly ICallGateProvider<bool> _handlesBoosts, _handlesEnemy;
    private readonly ICallGateProvider<float> _stopRange;
    private readonly ICallGateProvider<object> _dismountedForCombat;
    private readonly ICallGateSubscriber<object> _tripStarted, _tripEnded;
    private readonly ICallGateSubscriber<bool> _isTrip, _willMount;
    private readonly ICallGateSubscriber<Dictionary<string, float>> _speeds;

    /// <summary>A Beeline trip is running (from its start/end events).</summary>
    internal bool TripActive { get; private set; }

    public Link(Plugin p)
    {
        _p = p;
        var pi = Plugin.PluginInterface;
        _handlesBoosts = pi.GetIpcProvider<bool>("GiddyUp.HandlesSpeedBuffs");
        _handlesEnemy  = pi.GetIpcProvider<bool>("GiddyUp.HandlesEnemyDismount");
        _dismountedForCombat = pi.GetIpcProvider<object>("GiddyUp.DismountedForCombat");
        _handlesBoosts.RegisterFunc(() => _p.Config.AutoSprint || _p.Config.KeepPeloton);
        _handlesEnemy.RegisterFunc(() => _p.Config.DismountOnEnemy);
        // For Beeline's "stop the trip when you target an enemy": how close it should be first, per your settings
        // (your attack range, melee option included), or -1 = at once ("as soon as you target it").
        _stopRange = pi.GetIpcProvider<float>("GiddyUp.EnemyStopRange");
        _stopRange.RegisterFunc(() =>
        {
            if (_p.Config.EnemyMode == EnemyDismount.WhenTargeted)
                return -1f;
            _p.Range.Recheck();
            return _p.Range.Yalms;
        });

        _tripStarted = pi.GetIpcSubscriber<object>("Beeline.TripStarted");
        _tripEnded   = pi.GetIpcSubscriber<object>("Beeline.TripEnded");
        _isTrip      = pi.GetIpcSubscriber<bool>("Beeline.IsTripActive");
        _willMount   = pi.GetIpcSubscriber<bool>("Beeline.WillMount");
        _speeds      = pi.GetIpcSubscriber<Dictionary<string, float>>("Beeline.GetLearnedSpeeds");
        _tripStarted.Subscribe(OnTripStarted);
        _tripEnded.Subscribe(OnTripEnded);

        // Loaded mid-trip, or after Beeline: catch up once.
        try { TripActive = _isTrip.InvokeFunc(); } catch { TripActive = false; }
        PullSpeeds();
    }

    public void Dispose()
    {
        _tripStarted.Unsubscribe(OnTripStarted);
        _tripEnded.Unsubscribe(OnTripEnded);
        _handlesBoosts.UnregisterFunc();
        _handlesEnemy.UnregisterFunc();
        _stopRange.UnregisterFunc();
    }

    /// <summary>During a trip: will Beeline mount on this leg? (Asked only when about to use Sprint/Peloton.)</summary>
    internal bool BeelineWillMount()
    {
        if (!TripActive)
            return false;
        try { return _willMount.InvokeFunc(); }
        catch { TripActive = false; return false; } // Beeline unloaded mid-trip
    }

    /// <summary>Tell Beeline we dismounted you for a fight (its trip ends).</summary>
    internal void DismountedForCombat()
    {
        try { _dismountedForCombat.SendMessage(); }
        catch (Exception e) { Plugin.Log.Debug(e, $"{Plugin.Tag} Couldn't notify Beeline"); }
    }

    private void OnTripStarted()
    {
        TripActive = true;
        _p.OnFoot.OnTripStarted();
        Plugin.Info("Beeline trip started: Beeline decides mounting, Giddy Up handles Sprint/Peloton on foot");
    }

    private void OnTripEnded()
    {
        TripActive = false;
        PullSpeeds(); // Beeline saves what it learned when a trip ends
    }

    /// <summary>Beeline's learned speeds, for each one it has measured at least once.</summary>
    private void PullSpeeds()
    {
        Dictionary<string, float> d;
        try { d = _speeds.InvokeFunc(); }
        catch { _p.Speeds.SetExternal(null); return; } // no Beeline: Giddy Up learns everything
        _p.Speeds.SetExternal(d);
    }
}
