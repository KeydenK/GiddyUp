namespace GiddyUp;

internal enum MoveMode { Run, Sprint, Jog, Peloton }

/// <summary>
/// Your real speeds and timings, learned while you play. Every decision uses these, never fixed numbers (the defaults
/// in the configuration are only where learning starts).
///
/// When Beeline is installed, every speed it has measured comes from it (it measures on clean, straight vnavmesh
/// moves) and Giddy Up doesn't measure that one again; Giddy Up learns the rest itself, plus what only it uses
/// (landing, getting off, action delay).
/// </summary>
internal sealed class Speeds
{
    private const int SlicesPerSample = 30; // 3 s of 10 Hz slices
    private const int Modes = 4;
    private readonly Plugin _p;
    private readonly float[][] _slices = new float[Modes][];
    private readonly int[] _count = new int[Modes];
    private readonly bool[] _fromBeeline = new bool[Modes];

    public Speeds(Plugin p)
    {
        _p = p;
        for (var i = 0; i < Modes; i++)
            _slices[i] = new float[SlicesPerSample];
    }

    private Configuration Config => _p.Config;

    internal bool FromBeeline(MoveMode mode) => _fromBeeline[(int)mode];

    internal float Get(MoveMode mode) => mode switch
    {
        MoveMode.Run    => Config.RunSpeed,
        MoveMode.Sprint => Config.SprintSpeed,
        MoveMode.Jog    => Config.JogSpeed,
        _               => Config.PelotonSpeed,
    };

    /// <summary>Beeline's learned speeds (null = no Beeline): used for each speed it has measured at least once.</summary>
    internal void SetExternal(Dictionary<string, float>? d)
    {
        Array.Clear(_fromBeeline);
        if (d == null)
            return;
        void Take(MoveMode mode, string key, Action<float> set)
        {
            if (d.TryGetValue(key + "Samples", out var n) && n > 0 && d.TryGetValue(key, out var v) && v is > 1f and < 200f)
            {
                set(v);
                _fromBeeline[(int)mode] = true;
                _count[(int)mode] = 0;
            }
        }
        Take(MoveMode.Run,     "Run",     v => Config.RunSpeed = v);
        Take(MoveMode.Sprint,  "Sprint",  v => Config.SprintSpeed = v);
        Take(MoveMode.Jog,     "Jog",     v => Config.JogSpeed = v);
        Take(MoveMode.Peloton, "Peloton", v => Config.PelotonSpeed = v);
        if (d.TryGetValue("SprintSeconds", out var sec))
            SeenSprintDuration(sec);
        Plugin.Info("Speeds from Beeline: " + string.Join(", ", Enum.GetValues<MoveMode>().Where(FromBeeline)));
        _p.MarkDirty();
    }

    /// <summary>
    /// One slice of movement (distance over time). Slices well below the speed already known for that mode are
    /// someone standing, turning or walking against a wall, and are skipped.
    /// </summary>
    internal void Slice(MoveMode mode, float distance, float seconds)
    {
        var m = (int)mode;
        if (_fromBeeline[m] || seconds is < 0.03f or > 0.5f)
            return;
        var speed = distance / seconds;
        if (speed < Get(mode) * 0.5f || speed > 200f)
            return;
        _slices[m][_count[m]++] = speed;
        if (_count[m] < SlicesPerSample)
            return;
        _count[m] = 0;
        // 80th percentile: brief slow-downs (a slight stick tilt, a bump) don't drag the value down.
        Array.Sort(_slices[m]); // the buffer starts over anyway: sort it in place
        Learn(mode, _slices[m][(int)(SlicesPerSample * 0.8f)]);
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
