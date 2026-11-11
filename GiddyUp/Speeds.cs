namespace GiddyUp;

internal enum MoveMode { Run, Sprint, Jog, Peloton, Ride, Fly }

/// <summary>
/// Your real speeds, learned while you play. Every decision uses these, never fixed numbers (the defaults in the
/// configuration are only where learning starts).
///
/// When Beeline is installed it is the one that learns, and Giddy Up reads its numbers instead of measuring the
/// same thing twice. (Beeline's side of that link comes in its next release; until then Giddy Up learns alone.)
/// </summary>
internal sealed class Speeds
{
    private const int SlicesPerSample = 30; // 3 s of 10 Hz slices
    private readonly Plugin _p;
    private readonly float[][] _slices = new float[6][];
    private readonly int[] _count = new int[6];

    public Speeds(Plugin p)
    {
        _p = p;
        for (var i = 0; i < _slices.Length; i++)
            _slices[i] = new float[SlicesPerSample];
    }

    private Configuration Config => _p.Config;

    /// <summary>False while another source (Beeline) provides the speeds: then nothing is measured here.</summary>
    internal bool LearnHere => true;

    internal float Get(MoveMode mode) => mode switch
    {
        MoveMode.Run     => Config.RunSpeed,
        MoveMode.Sprint  => Config.SprintSpeed,
        MoveMode.Jog     => Config.JogSpeed,
        MoveMode.Peloton => Config.PelotonSpeed,
        MoveMode.Ride    => Config.RideSpeed,
        _                => Config.FlySpeed,
    };

    /// <summary>
    /// One slice of movement (distance over time). Slices well below the speed already known for that mode are
    /// someone standing, turning or walking against a wall, and are skipped.
    /// </summary>
    internal void Slice(MoveMode mode, float distance, float seconds)
    {
        if (!LearnHere || seconds is < 0.03f or > 0.5f)
            return;
        var speed = distance / seconds;
        if (speed < Get(mode) * 0.5f || speed > 200f)
            return;
        var m = (int)mode;
        _slices[m][_count[m]++] = speed;
        if (_count[m] < SlicesPerSample)
            return;
        _count[m] = 0;
        // 80th percentile: brief slow-downs (a slight stick tilt, a bump) don't drag the value down.
        var sorted = (float[])_slices[m].Clone();
        Array.Sort(sorted);
        Learn(mode, sorted[(int)(SlicesPerSample * 0.8f)]);
    }

    /// <summary>Forget partial measurements (the way of moving changed, or you stopped).</summary>
    internal void Reset(MoveMode mode) => _count[(int)mode] = 0;

    private void Learn(MoveMode mode, float speed)
    {
        switch (mode)
        {
            case MoveMode.Run:     Config.RunSpeed     = Blend(Config.RunSpeed,     Config.RunSamples++,     speed); break;
            case MoveMode.Sprint:  Config.SprintSpeed  = Blend(Config.SprintSpeed,  Config.SprintSamples++,  speed); break;
            case MoveMode.Jog:     Config.JogSpeed     = Blend(Config.JogSpeed,     Config.JogSamples++,     speed); break;
            case MoveMode.Peloton: Config.PelotonSpeed = Blend(Config.PelotonSpeed, Config.PelotonSamples++, speed); break;
            case MoveMode.Ride:    Config.RideSpeed    = Blend(Config.RideSpeed,    Config.RideSamples++,    speed); break;
            case MoveMode.Fly:     Config.FlySpeed     = Blend(Config.FlySpeed,     Config.FlySamples++,     speed); break;
        }
        Plugin.Info($"Learned {mode}: {speed:0.0} y/s -> {Get(mode):0.0}");
        _p.MarkDirty();
    }

    /// <summary>A landing: descended this many yalms in that time, then took this long to get off.</summary>
    internal void LearnLanding(float height, float descentSeconds, float getOffSeconds)
    {
        if (height >= 2f && descentSeconds is > 0.1f and < 30f)
        {
            var speed = height / descentSeconds;
            if (speed is > 0.5f and < 200f)
                Config.DescentSpeed = Blend(Config.DescentSpeed, Config.DescentSamples++, speed);
        }
        if (getOffSeconds is > 0f and < 10f)
            Config.GetOffSeconds = Blend(Config.GetOffSeconds, Config.GetOffSamples++, getOffSeconds);
        Plugin.Info($"Learned landing: {height:0.0} y down in {descentSeconds:0.00} s, off in {getOffSeconds:0.00} s "
                  + $"-> descent {Config.DescentSpeed:0.0} y/s, get off {Config.GetOffSeconds:0.00} s");
        _p.MarkDirty();
    }

    /// <summary>Seconds from pressing Dismount to being on your feet, from this height above the ground.</summary>
    internal float LandingSeconds(float height)
        => Math.Max(0f, height) / Math.Max(0.5f, Config.DescentSpeed) + Config.GetOffSeconds;

    internal void LearnActionLag(float seconds)
    {
        if (seconds is < 0.01f or > 2f)
            return;
        Config.ActionLagSeconds = Blend(Config.ActionLagSeconds, Config.ActionLagSamples++, seconds);
        _p.MarkDirty();
    }

    internal void SeenSprintDuration(float remaining)
    {
        if (remaining > Config.SprintSeconds + 0.5f && remaining < 120f)
        {
            Config.SprintSeconds = remaining;
            _p.MarkDirty();
        }
    }

    /// <summary>Running average that settles: early samples move it a lot, later ones less (never under 1/4).</summary>
    private static float Blend(float old, int samples, float sample)
    {
        var a = Math.Max(0.25f, 1f / (samples + 1));
        return old + (sample - old) * a;
    }
}
