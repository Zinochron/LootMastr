using System;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>
/// The second currency, and the one with a rate limit.
///
/// Books arrive one per fight per clear and are spent the week they cover something. Tomestones
/// arrive at a flat weekly cap that no amount of clearing changes, which makes them the only thing
/// in this plugin a player cannot go faster at. That is why they belong in the forecast: a group
/// can be done with raid coffers in week five and still be four weeks off a finished set.
///
/// Pure arithmetic, same as <see cref="BookLedger"/> — no game references, so the harness can
/// assert on every line of it.
/// </summary>
public static class TomeLedger
{
    /// <summary>What a number of weeks is worth. Everyone caps out; nobody is modelled as lazy.</summary>
    public static int Income(TierDefinition tier, int weeks) =>
        Math.Max(0, tier.TomestonesPerWeek) * Math.Max(0, weeks);

    /// <summary>What is already in the bank when the tier opens.</summary>
    public static int StartingBalance(TierDefinition tier) => Income(tier, tier.PriorTomeWeeks);

    public static bool CanAfford(PlayerPlan player, int cost) => cost > 0 && player.Tomes >= cost;

    public static bool Pay(PlayerPlan player, int cost)
    {
        if (!CanAfford(player, cost))
            return false;

        player.Tomes -= cost;
        return true;
    }

    /// <summary>Everything this player still has to buy, added up.</summary>
    public static int Outstanding(PlayerPlan player)
    {
        var total = 0;

        foreach (var need in player.TomeOpen)
            total += need.Cost;

        return total;
    }

    /// <summary>
    /// The week they will have collected enough for everything they still owe. 0 when they owe
    /// nothing.
    ///
    /// A closed form rather than a reading off the simulation, and deliberately so: the order
    /// purchases are made in cannot change which week the <i>last</i> one lands, because income is
    /// flat. Two independent routes to the same number, and the harness asserts they agree — a
    /// disagreement means one of them has a bug rather than that either is a matter of opinion.
    /// </summary>
    public static int WeekAffordedBy(TierDefinition tier, PlayerPlan player)
    {
        var owed = Outstanding(player);
        if (owed <= 0)
            return 0;

        var shortfall = owed - player.Tomes;
        if (shortfall <= 0)
            return 1;

        var perWeek = Math.Max(1, tier.TomestonesPerWeek);
        return (shortfall + perWeek - 1) / perWeek;
    }
}
