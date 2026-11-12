using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace GiddyUp;

/// <summary>Small, allocation-free reads of the game state shared by the features.</summary>
internal static unsafe class Game
{
    // Verified IDs (game data): general actions, Peloton, statuses.
    internal const uint MountRoulette = 9, Dismount = 23, Sprint = 4;
    internal const uint PelotonAction = 7557;
    internal const uint SprintStatus = 50, PelotonStatus = 1199, JogStatus = 4209;

    internal static BattleChara* Player => Control.GetLocalPlayer();

    internal static bool IsMounted
        => Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];

    internal static bool MountComing
        => Plugin.Condition[ConditionFlag.Mounting] || Plugin.Condition[ConditionFlag.Mounting71]
        || Plugin.Condition[ConditionFlag.MountOrOrnamentTransition];

    /// <summary>
    /// States in which Giddy Up must not use anything: busy (talking, cutscene, crafting, gathering...), casting,
    /// changing zone, jumping, dead, or a mount already on its way. Just flag reads.
    /// </summary>
    internal static bool Busy()
    {
        var c = Plugin.Condition;
        return c[ConditionFlag.Casting] || c[ConditionFlag.Casting87] || MountComing
            || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] || c[ConditionFlag.Jumping61]
            || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.OccupiedSummoningBell] || c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied30]
            || c[ConditionFlag.Occupied33] || c[ConditionFlag.Occupied38] || c[ConditionFlag.Occupied39]
            || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
            || c[ConditionFlag.Crafting] || c[ConditionFlag.ExecutingCraftingAction] || c[ConditionFlag.PreparingToCraft]
            || c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction] || c[ConditionFlag.Fishing]
            || c[ConditionFlag.Unconscious] || c[ConditionFlag.Emoting]
            || c[ConditionFlag.InThatPosition] || c[ConditionFlag.CarryingObject] || c[ConditionFlag.CarryingItem]
            || c[ConditionFlag.Transformed] || c[ConditionFlag.Performing] || c[ConditionFlag.LoggingOut];
    }

    /// <summary>Remaining seconds of a status on you (0 if you don't have it; permanent statuses count as a long time).</summary>
    internal static float StatusLeft(BattleChara* player, uint statusId)
    {
        var sm = ((Character*)player)->GetStatusManager();
        if (sm == null)
            return 0;
        var i = sm->GetStatusIndex(statusId, 0xE0000000);
        if (i < 0)
            return 0;
        var left = sm->Status[i].RemainingTime;
        return left > 0 ? left : 999f;
    }

    internal static bool Ready(ActionType type, uint id)
        => ActionManager.Instance()->GetActionStatus(type, id) == 0;

    /// <summary>True while Giddy Up itself is using something (so it doesn't count as "you're still busy").</summary>
    internal static bool OwnCall { get; private set; }

    internal static bool Use(ActionType type, uint id)
    {
        OwnCall = true;
        try { return ActionManager.Instance()->UseAction(type, id); }
        finally { OwnCall = false; }
    }

    /// <summary>
    /// Your global cooldown right now, in ms, as the game computes it for a GCD action of your job (skill/spell speed
    /// included). Without one (no job action known yet), the GCD's usual 2.5 s.
    /// </summary>
    internal static float GcdMs(uint gcdActionId)
    {
        var ms = gcdActionId != 0 ? ActionManager.GetAdjustedRecastTime(ActionType.Action, gcdActionId, true) : 0;
        return ms > 0 ? ms : 2500f;
    }

    internal static string ActionName(uint id)
        => Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(id, out var a) ? a.Name.ExtractText() : $"#{id}";
}
