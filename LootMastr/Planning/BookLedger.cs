using System;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>
/// Books of another fight traded in to cover part of a purchase.
///
/// <paramref name="Books"/> is what was handed over, <paramref name="Covered"/> what it bought —
/// the two differ whenever a tier's trade is not one for one.
/// </summary>
public readonly record struct BookTrade(int FromEncounter, int Books, int Covered);

/// <summary>
/// What a player can pay for with the books they hold, including books traded in from another
/// fight. Pure arithmetic, shared by the simulator and the books grid so the two can never
/// disagree about whether someone can afford something.
/// </summary>
public static class BookLedger
{
    /// <summary>
    /// Books of <paramref name="fromEncounter"/> that are genuinely spare — the ones not already
    /// spoken for by something this player still needs from that fight.
    ///
    /// Reserving matters: the last fight's books convert into anything, but they are also what buys
    /// the weapon. Converting them freely would have the simulator trade away a weapon nobody can
    /// then afford, and report a finish date that never happens.
    /// </summary>
    public static int Spare(TierDefinition tier, PlayerPlan player, int fromEncounter)
    {
        var held = Held(player, fromEncounter);
        if (held == 0)
            return 0;

        var reserved = 0;

        foreach (var need in player.Open)
        {
            var cost = CostOf(tier, need);
            if (cost != null && cost.Encounter == fromEncounter)
                reserved += cost.Cost;
        }

        return Math.Max(0, held - reserved);
    }

    /// <summary>Books usable towards this fight's costs, own plus anything convertible into them.</summary>
    public static int Available(TierDefinition tier, PlayerPlan player, int encounter)
    {
        var total = Held(player, encounter);

        var source = tier.ConvertibleSourceFor(encounter);
        if (source == null)
            return total;

        var ratio = Math.Max(1, RatioFor(tier, source.Value));
        return total + (Spare(tier, player, source.Value) / ratio);
    }

    public static bool CanAfford(TierDefinition tier, PlayerPlan player, BookCost cost) =>
        cost.Cost > 0 && Available(tier, player, cost.Encounter) >= cost.Cost;

    public static bool Pay(TierDefinition tier, PlayerPlan player, BookCost cost) =>
        Pay(tier, player, cost, out _);

    /// <summary>
    /// Spends the books. Own books go first and only the shortfall is converted, so nothing is
    /// traded away that did not have to be.
    ///
    /// <paramref name="traded"/> reports the conversion when there was one. A plan that says "buy
    /// the head piece with three second-fight books" is a different instruction depending on whether
    /// one of those three has to be traded for first, and that is not visible from the counts.
    /// </summary>
    public static bool Pay(TierDefinition tier, PlayerPlan player, BookCost cost, out BookTrade? traded)
    {
        traded = null;

        if (!CanAfford(tier, player, cost))
            return false;

        var encounter = Clamp(cost.Encounter);
        var fromOwn = Math.Min(player.Tokens[encounter], cost.Cost);

        player.Tokens[encounter] -= fromOwn;

        var shortfall = cost.Cost - fromOwn;
        if (shortfall == 0)
            return true;

        var source = tier.ConvertibleSourceFor(cost.Encounter);
        if (source == null)
            return true;

        var ratio = Math.Max(1, RatioFor(tier, source.Value));
        var spent = shortfall * ratio;

        player.Tokens[Clamp(source.Value)] -= spent;
        traded = new BookTrade(source.Value, spent, shortfall);

        return true;
    }

    /// <summary>The book cost of one open need, whichever kind it is.</summary>
    public static BookCost? CostOf(TierDefinition tier, OpenNeed need) =>
        need.IsUpgrade ? tier.CostForUpgrade(need.Side) : tier.CostForSlot(need.Slot);

    private static int Held(PlayerPlan player, int encounter)
    {
        var index = Clamp(encounter);
        return index == 0 ? 0 : player.Tokens[index];
    }

    private static int RatioFor(TierDefinition tier, int fromEncounter)
    {
        foreach (var conversion in tier.Conversions)
        {
            if (conversion.FromEncounter == fromEncounter)
                return conversion.Ratio;
        }

        return 1;
    }

    private static int Clamp(int encounter) => Math.Clamp(encounter, 0, PlayerPlan.MaxEncounters);
}
