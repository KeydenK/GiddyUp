using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace GiddyUp;

/// <summary>Settings: a short status header, then one tab per topic. Long explanations live in (?) markers.</summary>
internal sealed unsafe class ConfigWindow : Window
{
    private const float ContentWidth = 440;
    private readonly Plugin _plugin;
    private long _depsCheckedAt = long.MinValue / 2;
    private bool _beeline;

    public ConfigWindow(Plugin plugin)
        : base("Giddy Up###GiddyUpConfig", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
        => _plugin = plugin;

    private Configuration Config => _plugin.Config;

    public override void Draw()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ContentWidth * ImGuiHelpers.GlobalScale);
        try
        {
            DrawStatus();
            ImGuiHelpers.ScaledDummy(4);
            using var tabs = ImRaii.TabBar("GiddyUpTabs");
            if (!tabs)
                return;
            using (var tab = ImRaii.TabItem("On foot"))
                if (tab) DrawOnFoot();
            using (var tab = ImRaii.TabItem("Mounting"))
                if (tab) DrawMounting();
            using (var tab = ImRaii.TabItem("Dismounting"))
                if (tab) DrawDismounting();
            using (var tab = ImRaii.TabItem("Advanced"))
                if (tab) DrawAdvanced();
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    // ---------------- Status ----------------

    private void DrawStatus()
    {
        if (Game.Player == null)
        {
            StatusLine(ImGuiColors.DalamudGrey, FontAwesomeIcon.Bed, "Not logged in");
            return;
        }
        if (Game.IsMounted)
        {
            var m = _plugin.Mounted;
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.Horse, m.Now switch
            {
                Mounted.State.Approaching => $"Mounted: enemy at {m.Gap:0} y, dismounting at {m.DismountAt:0} y",
                Mounted.State.NoSight     => "Mounted: enemy in range, waiting for line of sight",
                Mounted.State.WaitingStill => "Mounted: enemy targeted, dismounting once you stop moving",
                _                         => "Mounted",
            });
        }
        else if (_plugin.OnFoot.Watching)
        {
            var why = _plugin.OnFoot.MountReason;
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.Running,
                       why != null ? $"On foot: will mount ({why})" : "On foot: Sprint / Peloton ready to go");
        }
        else
            StatusLine(ImGuiColors.DalamudGrey, FontAwesomeIcon.PauseCircle, "Resting (in combat, or nothing to do)");

        StatusLine(ImGuiColors.DalamudGrey, FontAwesomeIcon.Crosshairs,
                   $"Your attack range: {_plugin.Range.Yalms:0} y ({_plugin.Range.ActionName})");
        // Listing installed plugins allocates: once a second, not every frame.
        if (Environment.TickCount64 - _depsCheckedAt > 1000)
        {
            _depsCheckedAt = Environment.TickCount64;
            _beeline = Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Beeline" && p.IsLoaded);
        }
        if (_beeline)
        {
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.Link, "Beeline found");
            Help("During Beeline trips, Beeline decides when to mount (it knows the route) and Giddy Up handles Sprint, "
               + "Peloton and dismounting. Beeline's learned speeds are used here too (Advanced tab), so they're not "
               + "measured twice.");
        }
        else
            Companion("Beeline", "https://raw.githubusercontent.com/KeydenK/Beeline/main/repo.json",
                      "Point at anything on your map and press one button: Beeline takes you there the quickest way "
                    + "(teleport, aethernet, mount, flying), right up to quest NPCs and objects. Together, Beeline "
                    + "decides when to mount on its trips, Giddy Up handles Sprint, Peloton and getting you off for a "
                    + "fight, and Giddy Up uses Beeline's learned speeds.");
        if (_plugin.Link.TripActive)
            StatusLine(ImGuiColors.DalamudYellow, FontAwesomeIcon.Route, "Beeline trip: Beeline decides mounting, Giddy Up the rest");
        if (!_plugin.ActionDismount.HookWorks)
            StatusLine(ImGuiColors.DalamudRed, FontAwesomeIcon.ExclamationTriangle,
                       "\"Dismount when using an action\" couldn't start (see /xllog)");
    }

    // ---------------- Tabs ----------------

    private void DrawOnFoot()
    {
        Check("Sprint when it helps", () => Config.AutoSprint, v => Config.AutoSprint = v,
              "Out of combat, when you're moving and Sprint is ready, Giddy Up uses it if it's faster than how you move "
            + "right now (Jog and Peloton included). Not when a mount is about to be summoned.");
        Check("Keep Peloton up", () => Config.KeepPeloton, v => Config.KeepPeloton = v,
              "Archer, Bard, Machinist and Dancer (level 20+), out of combat, while moving. Peloton goes on in Sprint's "
            + "last moments so it takes over when Sprint ends, and is renewed just before it runs out.");
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var move = Config.MoveSeconds;
        if (ImGui.SliderFloat("Moving for at least", ref move, 0f, 2f, "%.1f s"))
            Config.MoveSeconds = move;
        SaveWhenReleased();
        Help("How long you must have been moving before Sprint or Peloton is used, so a small step doesn't spend them.");
    }

    private void DrawMounting()
    {
        Hint("Mount up automatically where mounts are allowed (never in duties), after:");
        Trigger("tzone", "A zone change or teleport", () => Config.MountAfterZoneChange, v => Config.MountAfterZoneChange = v,
                () => Config.ZoneChangeTiming, v => Config.ZoneChangeTiming = v);
        Trigger("tcombat", "Combat", () => Config.MountAfterCombat, v => Config.MountAfterCombat = v,
                () => Config.CombatTiming, v => Config.CombatTiming = v);
        Trigger("tgather", "Gathering or fishing", () => Config.MountAfterGathering, v => Config.MountAfterGathering = v,
                () => Config.GatheringTiming, v => Config.GatheringTiming = v,
                "'When you move' by default: after gathering many people teleport away, and the mount would be in the way.");
        Trigger("tcraft", "Crafting", () => Config.MountAfterCrafting, v => Config.MountAfterCrafting = v,
                () => Config.CraftingTiming, v => Config.CraftingTiming = v,
                "'When you move' by default: the crafting log reopens after each craft while you stand still.");
        Trigger("ttalk", "Talking to someone or using something", () => Config.MountAfterTalking, v => Config.MountAfterTalking = v,
                () => Config.TalkingTiming, v => Config.TalkingTiming = v,
                "After a conversation, or after interacting with an object (quest objects, switches, chests...). 'When you "
              + "move' by default: someone with several quests reopens a conversation right after the last one.");
        ImGuiHelpers.ScaledDummy(2);

        Check("Mount as soon as you're done", () => Config.MountSmart, v => Config.MountSmart = v,
              "Giddy Up waits until you're idle: not casting, and no action used for one of your global cooldowns "
            + "since your last cast ended. So after a fight, a Pictomancer can paint motifs, a healer can top "
            + "people up, and you can press your buffs: summoning a mount is a cast that would block all that. Turn "
            + "this off to pick a fixed delay instead.");
        if (!Config.MountSmart)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            var delay = Config.MountDelaySeconds;
            if (ImGui.SliderFloat("Delay", ref delay, 0f, 10f, "%.1f s"))
                Config.MountDelaySeconds = delay;
            SaveWhenReleased();
            Help("Wait this long after the fight, zone change, gathering... before summoning (still never while you're "
               + "casting or busy).");
            ImGui.Unindent();
        }
        DrawMountPicker();
    }

    private void DrawDismounting()
    {
        Check("Dismount when you use an action", () => Config.DismountOnAction, v => Config.DismountOnAction = v,
              "Press any action the game won't let you use on a mount: you get off (after landing, if you were "
            + "flying). A press that wouldn't work anyway "
            + "(target out of range or sight, on cooldown) leaves you on your mount.");
        if (Config.DismountOnAction)
        {
            ImGui.Indent();
            Check("Then use the action", () => Config.ReplayAction, v => Config.ReplayAction = v,
                  "Once you're off, Giddy Up uses the action you pressed. Turn this off to only get off the mount and "
                + "press it again yourself. Either way, if any other action is used first after you land, Giddy Up "
                + "doesn't use yours on top of it.");
            ImGui.Unindent();
        }
        Check("Dismount for an enemy you target", () => Config.DismountOnEnemy, v => Config.DismountOnEnemy = v);
        if (Config.DismountOnEnemy)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
            var mode = (int)Config.EnemyMode;
            if (ImGui.Combo("##enemymode", ref mode, EnemyItems))
            {
                Config.EnemyMode = (EnemyDismount)mode;
                _plugin.SaveConfig();
            }
            Help("'Within your attack range': you stay on the mount while you approach, and land right where you can "
               + "attack. The range is the longest of the attacks you can always open with on your job (shown at the "
               + "top). Flying, Giddy Up starts landing early, by how long landing takes from your height.");
            if (Config.EnemyMode == EnemyDismount.InRange)
                Check("Melee jobs: land at melee range", () => Config.MeleeAtMeleeRange, v => Config.MeleeAtMeleeRange = v,
                      "Tanks and melee DPS can open from about 20 y (Shield Lob, Tomahawk, Piercing Talon, Enpi...), so "
                    + "that's where Giddy Up gets you off by default. Turn this on to stay mounted until you're within "
                    + "reach of your close-range attacks instead: your weaponskills, or a line/cone like Doom Spike "
                    + "(10 y) or Fuga (8 y) when you have one. Ranged jobs and casters aren't affected.");
            Hint("Flying: hold still to land. Moving stops the game's landing, so Giddy Up waits until you stop, and "
               + "stops a Beeline trip to let you land.");
            Check("Remind me on screen", () => Config.HoldStillNotice, v => Config.HoldStillNotice = v,
                  "Shows \"hold still to land\" once per enemy when it's in range and you're still flying around.");
            Check("Riding: wait until you stop moving", () => Config.RideDismountWhenStill, v => Config.RideDismountWhenStill = v,
                  "On the ground, Giddy Up dismounts only once you hold still (it doesn't forget meanwhile). Off: as "
                + "soon as the enemy is in range, even while you move.");
            ImGui.Unindent();
        }
    }

    private void DrawAdvanced()
    {
        ImGui.TextUnformatted("Learned from how you play");
        Help("Giddy Up measures your real speeds and timings and decides with them (your speeds may differ from the "
           + "game's usual ones). The numbers below are refined as you play; 'default' means not measured yet.");
        using (var table = ImRaii.Table("learned", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            if (table)
            {
                var sp = _plugin.Speeds;
                Row("Running",            $"{Config.RunSpeed:0.0} y/s", Config.RunSamples, sp.FromBeeline(MoveMode.Run));
                Row("Sprint",             $"{Config.SprintSpeed:0.0} y/s for {Config.SprintSeconds:0} s", Config.SprintSamples, sp.FromBeeline(MoveMode.Sprint));
                Row("Jog (after Sprint)", $"{Config.JogSpeed:0.0} y/s", Config.JogSamples, sp.FromBeeline(MoveMode.Jog));
                Row("Peloton",            $"{Config.PelotonSpeed:0.0} y/s", Config.PelotonSamples, sp.FromBeeline(MoveMode.Peloton));
                Row("Landing (descent)",  $"{Config.DescentSpeed:0.0} y/s down", Config.DescentSamples);
                Row("Getting off",        $"{Config.GetOffSeconds:0.00} s", Config.GetOffSamples);
                Row("Action delay",       $"{Config.ActionLagSeconds * 1000:0} ms", Config.ActionLagSamples);
            }
        }
        if (ImGui.Button("Reset to defaults"))
        {
            Config.ResetLearned();
            _plugin.SaveConfig();
        }

        ImGuiHelpers.ScaledDummy(6);
        Check("Write troubleshooting details to the Dalamud log", () => Config.DetailedLog, v => Config.DetailedLog = v,
              "Each thing Giddy Up does and why, as [GiddyUp] lines in /xllog. Include them when you report a problem.");
    }

    // ---------------- Mount picker ----------------

    private List<(uint Id, string Name, string Label)>? _mounts;
    private string _searchHint = "";
    private string _mountFilter = "";
    private uint _labelFor = uint.MaxValue;
    private string _label = "Mount Roulette";

    private string MountLabel()
    {
        var key = Config.MountKind == MountChoice.Specific ? Config.MountId : 0;
        if (key != _labelFor)
        {
            _labelFor = key;
            _label = key != 0 ? MountName(key) : "Mount Roulette";
        }
        return _label;
    }

    private static string MountName(uint id)
    {
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Mount>().TryGetRow(id, out var m))
            return $"Mount #{id}";
        var n = m.Singular.ExtractText();
        return n.Length > 0 ? char.ToUpper(n[0]) + n[1..] : $"Mount #{id}";
    }

    private void DrawMountPicker()
    {
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("Mount", MountLabel(), ImGuiComboFlags.HeightLarge))
        {
            if (!combo)
                _mounts = null; // rebuilt next time, so newly unlocked mounts show up
            else
            {
                if (_mounts == null)
                {
                    var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                    _mounts = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Mount>()
                        .Where(m => m.RowId != 0 && !m.Singular.IsEmpty && ps != null && ps->IsMountUnlocked(m.RowId))
                        .Select(m => MountName(m.RowId) is var n ? (m.RowId, n, $"{n}##m{m.RowId}") : default) // labels built once
                        .OrderBy(m => m.Item2, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _mountFilter = "";
                    _searchHint = $"Search your {_mounts.Count} mounts";
                }
                if (ImGui.Selectable("Mount Roulette", Config.MountKind != MountChoice.Specific))
                    SetMount(MountChoice.Roulette, 0);
                ImGui.Separator();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##mountsearch", _searchHint, ref _mountFilter, 64);
                foreach (var (id, name, label) in _mounts)
                {
                    if (_mountFilter.Length > 0 && !name.Contains(_mountFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable(label, Config.MountKind == MountChoice.Specific && Config.MountId == id))
                        SetMount(MountChoice.Specific, id);
                }
            }
        }
        Help("If your chosen mount can't be summoned somewhere, Giddy Up uses Mount Roulette there.");
    }

    private void SetMount(MountChoice kind, uint id)
    {
        Config.MountKind = kind;
        Config.MountId = id;
        _plugin.SaveConfig();
    }

    // ---------------- Helpers ----------------

    private void Check(string label, Func<bool> get, Action<bool> set, string? help = null)
    {
        var v = get();
        if (ImGui.Checkbox(label, ref v))
        {
            set(v);
            _plugin.SaveConfig();
        }
        if (help != null)
            Help(help);
    }

    private static void Help(string text)
    {
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(text);
    }

    private static readonly string[] EnemyItems = ["once it's within your attack range", "as soon as you target it"];

    private static readonly string[] TimingItems = ["right away", "when you move"];

    /// <summary>
    /// The companion plugin isn't installed: say what it adds, and how to get it (its repository link, with a Copy
    /// button, in Dalamud's custom repositories, then install it).
    /// </summary>
    private static void Companion(string name, string repoUrl, string whatItAdds)
    {
        StatusLine(ImGuiColors.DalamudViolet, FontAwesomeIcon.PuzzlePiece, $"Works great with {name} (not installed)");
        Help(whatItAdds);
        using var node = ImRaii.TreeNode($"How to get {name}##get{name}");
        if (!node)
            return;
        ImGui.TextUnformatted("1. Type /xlsettings and open the Experimental tab.");
        ImGui.TextUnformatted("2. Under Custom Plugin Repositories, paste this link, click +, then Save and Close:");
        var url = repoUrl;
        ImGui.SetNextItemWidth(330 * ImGuiHelpers.GlobalScale);
        ImGui.InputText($"##url{name}", ref url, 256, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy##copy{name}"))
            ImGui.SetClipboardText(repoUrl);
        ImGui.TextUnformatted($"3. Type /xlplugins, search for {name} and install it.");
    }

    /// <summary>One auto-mount trigger: its checkbox, then (when on) right away / when you move, on the same line.</summary>
    private void Trigger(string id, string label, Func<bool> get, Action<bool> set, Func<MountTiming> getTiming,
                         Action<MountTiming> setTiming, string? help = null)
    {
        var on = get();
        if (ImGui.Checkbox(label, ref on))
        {
            set(on);
            _plugin.SaveConfig();
        }
        if (on)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
            var t = (int)getTiming();
            if (ImGui.Combo($"##{id}", ref t, TimingItems))
            {
                setTiming((MountTiming)t);
                _plugin.SaveConfig();
            }
        }
        if (help != null)
            Help(help);
    }

    /// <summary>Sliders: save once you let go, not on every frame while dragging.</summary>
    private void SaveWhenReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            _plugin.SaveConfig();
    }

    private static void Row(string what, string value, int samples, bool fromBeeline = false)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(what);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextUnformatted(fromBeeline ? "from Beeline" : samples == 0 ? "default" : samples == 1 ? "1 sample" : $"{samples} samples");
    }

    private static void StatusLine(Vector4 color, FontAwesomeIcon icon, string text)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(icon.ToIconString());
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    private static void Hint(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextUnformatted(text);
    }
}
