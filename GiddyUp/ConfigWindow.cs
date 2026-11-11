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
            var s = _plugin.Mounted.Status;
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.Horse, s.Length > 0 ? $"Mounted: {s}" : "Mounted");
        }
        else if (_plugin.OnFoot.Watching)
        {
            var why = _plugin.OnFoot.MountReason;
            StatusLine(ImGuiColors.HealerGreen, FontAwesomeIcon.Running,
                       why != null ? $"On foot: will mount ({why})" : "On foot: Sprint / Peloton ready to go");
        }
        else
            StatusLine(ImGuiColors.DalamudGrey, FontAwesomeIcon.PauseCircle, "Resting (in combat, or nothing to do)");

        _plugin.Range.Ensure();
        StatusLine(ImGuiColors.DalamudGrey, FontAwesomeIcon.Crosshairs,
                   $"Your attack range: {_plugin.Range.Yalms:0} y ({_plugin.Range.ActionName})");
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
        {
            Config.MoveSeconds = move;
            _plugin.SaveConfig();
        }
        Help("How long you must have been moving before Giddy Up reacts (Sprint, Peloton, or a mount 'when you start "
           + "moving'), so a small step doesn't spend them.");
    }

    private void DrawMounting()
    {
        Hint("Mount up automatically, where mounts are allowed (never in duties):");
        Check("After a zone change or teleport", () => Config.MountAfterZoneChange, v => Config.MountAfterZoneChange = v);
        Check("After combat", () => Config.MountAfterCombat, v => Config.MountAfterCombat = v);
        Check("After gathering", () => Config.MountAfterGathering, v => Config.MountAfterGathering = v);

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var when = (int)Config.MountWhen;
        if (ImGui.Combo("When", ref when, ["when you start moving", "right away"]))
        {
            Config.MountWhen = (MountTiming)when;
            _plugin.SaveConfig();
        }
        Help("'When you start moving': you keep moving while the mount is summoned, and standing around (at a vendor, "
           + "waiting for a friend) doesn't put you on a mount for nothing.");
        DrawMountPicker();
    }

    private void DrawDismounting()
    {
        Check("Dismount when you use an action", () => Config.DismountOnAction, v => Config.DismountOnAction = v,
              "Press any action the game won't let you use on a mount: you get off and the action goes off as soon "
            + "as you're on your feet (after landing, if you were flying). A press that wouldn't work anyway "
            + "(target out of range or sight, on cooldown) leaves you on your mount.");
        Check("Dismount for an enemy you target", () => Config.DismountOnEnemy, v => Config.DismountOnEnemy = v);
        if (Config.DismountOnEnemy)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
            var mode = (int)Config.EnemyMode;
            if (ImGui.Combo("##enemymode", ref mode, ["once it's within your attack range", "as soon as you target it"]))
            {
                Config.EnemyMode = (EnemyDismount)mode;
                _plugin.SaveConfig();
            }
            Help("'Within your attack range': you stay on the mount while you approach, and land right where you can "
               + "attack. The range is the longest of the attacks you can always open with on your job (shown at the "
               + "top). Flying, Giddy Up starts landing early, by how long landing takes from your height.");
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
                Row("Running",            $"{Config.RunSpeed:0.0} y/s", Config.RunSamples);
                Row("Sprint",             $"{Config.SprintSpeed:0.0} y/s for {Config.SprintSeconds:0} s", Config.SprintSamples);
                Row("Jog (after Sprint)", $"{Config.JogSpeed:0.0} y/s", Config.JogSamples);
                Row("Peloton",            $"{Config.PelotonSpeed:0.0} y/s", Config.PelotonSamples);
                Row("Riding",             $"{Config.RideSpeed:0.0} y/s", Config.RideSamples);
                Row("Flying",             $"{Config.FlySpeed:0.0} y/s", Config.FlySamples);
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

    private List<(uint Id, string Name)>? _mounts;
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
                        .Select(m => (m.RowId, MountName(m.RowId)))
                        .OrderBy(m => m.Item2, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _mountFilter = "";
                }
                if (ImGui.Selectable("Mount Roulette", Config.MountKind != MountChoice.Specific))
                    SetMount(MountChoice.Roulette, 0);
                ImGui.Separator();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##mountsearch", $"Search your {_mounts.Count} mounts", ref _mountFilter, 64);
                foreach (var (id, name) in _mounts)
                {
                    if (_mountFilter.Length > 0 && !name.Contains(_mountFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable($"{name}##m{id}", Config.MountKind == MountChoice.Specific && Config.MountId == id))
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

    private static void Row(string what, string value, int samples)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(what);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextUnformatted(samples == 0 ? "default" : samples == 1 ? "1 sample" : $"{samples} samples");
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
