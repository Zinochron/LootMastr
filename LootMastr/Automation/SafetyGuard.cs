using Dalamud.Game.ClientState.Conditions;
using LootMastr.Data;

namespace LootMastr.Automation;

public readonly record struct GuardVerdict(bool Ok, string Reason)
{
    public static readonly GuardVerdict Pass = new(true, string.Empty);

    public static GuardVerdict Block(string reason) => new(false, reason);
}

/// <summary>
/// Everything that has to be true before LootMastr is allowed to touch the loot window. Each
/// verdict carries its reason so the UI can say why a button is dead rather than just greying it.
/// </summary>
public sealed class SafetyGuard
{
    private readonly PartyReader party;
    private readonly LootWindowReader loot;

    public SafetyGuard(PartyReader party, LootWindowReader loot)
    {
        this.party = party;
        this.loot = loot;
    }

    /// <summary>Can LootMastr assign this window's items right now?</summary>
    public GuardVerdict CheckAssign()
    {
        if (!Services.ClientState.IsLoggedIn)
            return GuardVerdict.Block("Not logged in.");

        if (Services.Condition[ConditionFlag.BetweenAreas] ||
            Services.Condition[ConditionFlag.BetweenAreas51])
            return GuardVerdict.Block("Zoning.");

        if (Services.Condition[ConditionFlag.WatchingCutscene] ||
            Services.Condition[ConditionFlag.WatchingCutscene78] ||
            Services.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return GuardVerdict.Block("In a cutscene.");

        if (!loot.WindowOpen)
            return GuardVerdict.Block("The loot window is not open.");

        if (!party.LocalPlayerIsLeader())
            return GuardVerdict.Block("Only the party leader can assign loot.");

        var items = loot.Read();
        if (items.Count == 0)
            return GuardVerdict.Block("Nothing in the loot window.");

        if (!loot.LootmasterActive(items))
        {
            return GuardVerdict.Block(
                "The party is not on the Lootmaster rule. It has to be set in the duty finder " +
                "settings before entering, and only works for a preformed party.");
        }

        return GuardVerdict.Pass;
    }

    /// <summary>Softer check for the things that only suggest, like the party chat announcement.</summary>
    public GuardVerdict CheckAnnounce()
    {
        if (!Services.ClientState.IsLoggedIn)
            return GuardVerdict.Block("Not logged in.");

        if (Services.Party.Length == 0)
            return GuardVerdict.Block("Not in a party.");

        return GuardVerdict.Pass;
    }
}
