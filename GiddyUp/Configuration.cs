namespace GiddyUp;

[Serializable]
public sealed class Configuration : Dalamud.Configuration.IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---------------- On foot ----------------
    /// <summary>Use Sprint when it's ready and faster than how you move right now (out of combat).</summary>
    public bool AutoSprint { get; set; } = true;
    /// <summary>Keep Peloton up on jobs that have it (Archer, Bard, Machinist, Dancer from level 20), out of combat.</summary>
    public bool KeepPeloton { get; set; } = true;
    /// <summary>How long you must have been moving before Giddy Up reacts (so a small step doesn't spend Sprint or summon a mount).</summary>
    public float MoveSeconds { get; set; } = 0.4f;

    // ---------------- Mounting ----------------
    public bool MountAfterZoneChange { get; set; } = true;
    public bool MountAfterCombat { get; set; } = true;
    public bool MountAfterGathering { get; set; } = true;
    public bool MountAfterCrafting { get; set; } = true;
    public bool MountAfterTalking { get; set; } = true;
    /// <summary>
    /// Per trigger: mount right away, or once you start moving. Moving suits the ones often followed by a teleport
    /// (gathering), a window that reopens (crafting) or another conversation (quest givers with several quests).
    /// </summary>
    public MountTiming ZoneChangeTiming { get; set; } = MountTiming.RightAway;
    public MountTiming CombatTiming { get; set; } = MountTiming.RightAway;
    public MountTiming GatheringTiming { get; set; } = MountTiming.WhenMoving;
    public MountTiming CraftingTiming { get; set; } = MountTiming.WhenMoving;
    public MountTiming TalkingTiming { get; set; } = MountTiming.WhenMoving;
    /// <summary>
    /// Smart (default): mount as soon as you're done (no cast, no action used for a GCD).
    /// Off: wait <see cref="MountDelaySeconds"/> instead.
    /// </summary>
    public bool MountSmart { get; set; } = true;
    public float MountDelaySeconds { get; set; } = 1f;
    public MountChoice MountKind { get; set; } = MountChoice.Roulette;
    public uint MountId { get; set; }

    // ---------------- Dismounting ----------------
    /// <summary>Using an action that can't be used on a mount gets you off it, then the action goes off.</summary>
    public bool DismountOnAction { get; set; } = true;
    /// <summary>...then use that action once you're on your feet. Off: only dismount.</summary>
    public bool ReplayAction { get; set; } = true;
    public bool DismountOnEnemy { get; set; } = true;
    public EnemyDismount EnemyMode { get; set; } = EnemyDismount.InRange;
    /// <summary>Tanks and melee DPS: use melee range instead of their ranged opener (Shield Lob, Piercing Talon...).</summary>
    public bool MeleeAtMeleeRange { get; set; }

    // ---------------- Learned (defaults are only starting values) ----------------
    public float RunSpeed { get; set; } = 6f;
    public int RunSamples { get; set; }
    public float SprintSpeed { get; set; } = 7.8f;
    public int SprintSamples { get; set; }
    public float SprintSeconds { get; set; } = 20f;
    public float JogSpeed { get; set; } = 7.2f;
    public int JogSamples { get; set; }
    public float PelotonSpeed { get; set; } = 7.2f;
    public int PelotonSamples { get; set; }
    /// <summary>Dismounting while flying, the character first descends to the ground: how fast (yalms per second, straight down).</summary>
    public float DescentSpeed { get; set; } = 10f;
    public int DescentSamples { get; set; }
    /// <summary>Then, once on the ground, how long until you're off the mount.</summary>
    public float GetOffSeconds { get; set; } = 1f;
    public int GetOffSamples { get; set; }
    /// <summary>From using an action to its effect showing up on you (server round trip).</summary>
    public float ActionLagSeconds { get; set; } = 0.3f;
    public int ActionLagSamples { get; set; }

    public void ResetLearned()
    {
        RunSpeed = 6f; SprintSpeed = 7.8f; SprintSeconds = 20f; JogSpeed = 7.2f; PelotonSpeed = 7.2f;
        DescentSpeed = 10f; GetOffSeconds = 1f; ActionLagSeconds = 0.3f;
        RunSamples = SprintSamples = JogSamples = PelotonSamples = DescentSamples = GetOffSamples = ActionLagSamples = 0;
    }

    /// <summary>Write what Giddy Up does (and why) to the Dalamud log at Information level. Always on in dev builds.</summary>
    public bool DetailedLog { get; set; }
}

public enum MountChoice { Roulette = 0, Specific = 2 }
public enum MountTiming { RightAway = 0, WhenMoving = 1 }
public enum EnemyDismount { InRange = 0, WhenTargeted = 1 }
