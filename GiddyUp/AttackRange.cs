using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GiddyUp;

/// <summary>
/// How far you can attack from on your current job: the longest range among the attacks you can always open a fight
/// with.
///
/// Counted: your job's own damaging spells and weaponskills (not role actions like Provoke) that are unlocked at your
/// level, that cost nothing or only MP, that share the global cooldown only (no charges or own recast), and that need
/// no combo, proc, gauge or stance first.
///
/// Cost: the game-data scan (about 40,000 actions) runs once per job on a background thread, when you log in or
/// change job; what's left for the game thread is a handful of candidates, checked when your job or level changes.
/// </summary>
internal sealed unsafe class AttackRange
{
    private readonly record struct Candidate(uint Id, byte Level, uint UnlockLink, string Name);

    private readonly Dictionary<uint, Task<List<Candidate>>> _byJob = new();
    private uint _forJob = uint.MaxValue, _forLevel;
    private bool _final;
    private long _pickedAt;

    /// <summary>Range in yalms (edge of your hitbox to edge of the target's), and the action it comes from.</summary>
    internal float Yalms { get; private set; } = 3f;
    internal uint ActionId { get; private set; }
    internal string ActionName { get; private set; } = "auto-attack";

    // SecondaryCostType values that mean "needs something first" (checked against game data): 11 Polyglot,
    // 32 a form/"Ready" status, 46 a status (procs like Verstone Ready, Thunderhead), 48/49 Umbral Ice/Astral Fire,
    // 58 a combo step, 75 Black/White Mana, 127 stacks. Other values (Ruin, Cascade, Shield Lob...) are harmless markers.
    private static readonly HashSet<byte> NeedsSomethingFirst = [11, 32, 46, 48, 49, 58, 75, 127];
    private const byte GlobalCooldownGroup = 58;
    private const uint BlueMage = 36;

    /// <summary>Start the background scan for this job now (login, job change), so it's ready before you need it.</summary>
    internal void Prefetch()
    {
        var player = Game.Player;
        if (player != null)
            Candidates(player->ClassJob);
    }

    /// <summary>Recomputes if your job or level changed (cheap compare otherwise). Call from the game thread.</summary>
    internal void Ensure()
    {
        var player = Game.Player;
        if (player == null)
            return;
        var job = (uint)player->ClassJob;
        var level = (uint)player->Level;
        // Blue Mage's active spells can change without a job/level change: recheck it every few seconds.
        if (_final && job == _forJob && level == _forLevel
            && (job != BlueMage || Environment.TickCount64 - _pickedAt < 5000))
            return;
        _pickedAt = Environment.TickCount64;
        if (job != _forJob || level != _forLevel)
        {
            _forJob = job;
            _forLevel = level;
            _final = false;
        }
        var task = Candidates(job);
        if (!task.IsCompletedSuccessfully)
            return; // still scanning (a few ms): keep the previous value meanwhile
        Pick(task.Result, job, level);
        _final = true;
    }

    private Task<List<Candidate>> Candidates(uint job)
    {
        if (!_byJob.TryGetValue(job, out var task) || task.IsFaulted)
            _byJob[job] = task = Task.Run(() => Scan(job));
        return task;
    }

    /// <summary>Background: game data only (no game memory).</summary>
    private static List<Candidate> Scan(uint job)
    {
        var list = new List<Candidate>();
        if (!Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(job, out var cj))
            return list;
        // ClassJobCategory has one column per class/job abbreviation (GLA, PLD, ...).
        var column = typeof(Lumina.Excel.Sheets.ClassJobCategory).GetProperty(cj.Abbreviation.ExtractText());
        if (column == null)
            return list;
        foreach (var a in Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            if (!a.IsPlayerAction || a.IsPvP || !a.CanTargetHostile || a.IsRoleAction
                || a.ActionCategory.RowId is not (2 or 3)                  // spell or weaponskill
                || a.ClassJobLevel == 0
                || a.PrimaryCostType is not (0 or 3 or 96)                 // nothing, MP, or Pictomancer's basic paint
                || NeedsSomethingFirst.Contains(a.SecondaryCostType)
                || a.CooldownGroup != GlobalCooldownGroup || a.AdditionalCooldownGroup != 0 || a.MaxCharges > 1
                || a.ActionCombo.RowId != 0
                || !a.ClassJobCategory.IsValid || column.GetValue(a.ClassJobCategory.Value) is not true)
                continue;
            list.Add(new Candidate(a.RowId, a.ClassJobLevel, a.UnlockLink.RowId, a.Name.ExtractText()));
        }
        return list;
    }

    /// <summary>Game thread: unlocked at your level (and, for Blue Mage, in your active set); the game's own range.</summary>
    private void Pick(List<Candidate> candidates, uint job, uint level)
    {
        float best = 3f;
        uint bestId = 0;
        var bestName = "auto-attack";
        var ui = UIState.Instance();
        HashSet<uint>? blueSet = null;
        if (job == BlueMage)
        {
            blueSet = [];
            var am = ActionManager.Instance();
            for (var i = 0; i < 24; i++)
                blueSet.Add(am->GetActiveBlueMageActionInSlot(i));
        }
        foreach (var c in candidates)
        {
            if (c.Level > level || (blueSet != null && !blueSet.Contains(c.Id)))
                continue;
            if (c.UnlockLink != 0 && (ui == null || !ui->IsUnlockLinkUnlockedOrQuestCompleted(c.UnlockLink, 0, true)))
                continue;
            // The game's own range for you (turns "weapon range" -1 into 25 y for bows/guns, 3 y for melee).
            var range = ActionManager.GetActionRange(c.Id);
            if (range > best || (range == best && bestId == 0))
            {
                best = range;
                bestId = c.Id;
                bestName = c.Name;
            }
        }
        var changed = bestId != ActionId || best != Yalms;
        Yalms = best;
        ActionId = bestId;
        ActionName = bestName;
        if (changed)
            Plugin.Info($"Attack range for job {job} lv{level}: {Yalms:0} y ({ActionName})");
    }
}
