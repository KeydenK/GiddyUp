using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GiddyUp;

/// <summary>
/// How far you can attack from on your current job: the longest range among the attacks you can always open a fight
/// with. Worked out once per login / job change / level up (from game data), then just read.
///
/// Counted: your job's own damaging spells and weaponskills (not role actions like Provoke) that are unlocked at your
/// level, that cost nothing or only MP, that share the global cooldown only (no charges or own recast), and that need
/// no combo, proc, gauge or stance first.
/// </summary>
internal sealed unsafe class AttackRange
{
    private bool _valid;
    private uint _forJob, _forLevel;

    /// <summary>Range in yalms (edge of your hitbox to edge of the target's), and the action it comes from.</summary>
    internal float Yalms { get; private set; } = 3f;
    internal uint ActionId { get; private set; }
    internal string ActionName { get; private set; } = "";

    // SecondaryCostType values that mean "needs something first" (checked against game data): 11 Polyglot,
    // 32 a form/"Ready" status, 46 a status (procs like Verstone Ready, Thunderhead), 48/49 Umbral Ice/Astral Fire,
    // 58 a combo step, 75 Black/White Mana, 127 stacks. Other values (Ruin, Cascade, Shield Lob...) are harmless markers.
    private static readonly HashSet<byte> NeedsSomethingFirst = [11, 32, 46, 48, 49, 58, 75, 127];
    private const byte GlobalCooldownGroup = 58;

    internal void Invalidate() => _valid = false;

    /// <summary>Recomputes if your job or level changed since last time (cheap check otherwise).</summary>
    internal void Ensure()
    {
        var player = Game.Player;
        if (player == null)
            return;
        var job = player->ClassJob;
        var level = player->Level;
        if (_valid && job == _forJob && level == _forLevel)
            return;
        _valid = true;
        _forJob = job;
        _forLevel = level;
        Compute(job, level);
    }

    private void Compute(uint job, uint level)
    {
        Yalms = 3f;
        ActionId = 0;
        ActionName = "auto-attack";
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(job, out var cj))
            return;
        // ClassJobCategory has one column per class/job abbreviation (GLA, PLD, ...).
        var column = typeof(Lumina.Excel.Sheets.ClassJobCategory).GetProperty(cj.Abbreviation.ExtractText());
        if (column == null)
            return;
        var ui = UIState.Instance();
        foreach (var a in Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            if (!a.IsPlayerAction || a.IsPvP || !a.CanTargetHostile || a.IsRoleAction
                || a.ActionCategory.RowId is not (2 or 3)                  // spell or weaponskill
                || a.ClassJobLevel == 0 || a.ClassJobLevel > level
                || a.PrimaryCostType is not (0 or 3 or 96)                 // nothing, MP, or Pictomancer's basic paint
                || NeedsSomethingFirst.Contains(a.SecondaryCostType)
                || a.CooldownGroup != GlobalCooldownGroup || a.AdditionalCooldownGroup != 0 || a.MaxCharges > 1
                || a.ActionCombo.RowId != 0
                || !a.ClassJobCategory.IsValid || column.GetValue(a.ClassJobCategory.Value) is not true)
                continue;
            if (a.UnlockLink.RowId != 0 && (ui == null || !ui->IsUnlockLinkUnlockedOrQuestCompleted(a.UnlockLink.RowId, 0, true)))
                continue;
            // The game's own range for you (turns "weapon range" -1 into 25 y for bows/guns, 3 y for melee).
            var range = ActionManager.GetActionRange(a.RowId);
            if (range > Yalms || (range == Yalms && ActionId == 0))
            {
                Yalms = range;
                ActionId = a.RowId;
                ActionName = a.Name.ExtractText();
            }
        }
        Plugin.Info($"Attack range for job {cj.Abbreviation.ExtractText()} lv{level}: {Yalms:0} y ({ActionName})");
    }
}
