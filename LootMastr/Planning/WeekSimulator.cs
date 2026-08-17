using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>One thing the forecast expects to change hands, and when.</summary>
public readonly record struct PlannedAward(
    int Week,
    int Encounter,
    GearSlot? Slot,
    GearSide? Upgrade,
    string PlayerKey,
    string PlayerName,
    bool Bought,
    IReadOnlyList<AwardCandidate>? Considered = null,
    string? Why = null,
    BookTrade? Traded = null)
{
    /// <summary>
    /// What this is, in the words the plan is read in.
    ///
    /// A material is named after the piece it upgrades, not after its side. Three armour materials
    /// all called "Left upgrade" is the same line three times, and a reader is right to think the
    /// plan has counted something twice.
    /// </summary>
    public string What => Upgrade != null
                              ? Slot != null ? $"{Slot.Value.CofferLabel()} upgrade" : $"{Upgrade} upgrade"
                              : Slot?.CofferLabel() ?? "?";
}

public sealed record SimulationResult(
    int LastFinishWeek,
    IReadOnlyDictionary<string, int> FinishWeeks,
    IReadOnlyList<PlannedAward> Awards,
    int Horizon)
{
    /// <summary>True for a week the simulation never reached, i.e. "not within the horizon".</summary>
    public bool BeyondHorizon(int week) => week > Horizon;
}

/// <summary>
/// Plays the rest of the tier forward and reports when everyone would be done. Pure calculation:
/// nothing here touches the game, so it can be lifted into a console harness and asserted on.
///
/// Weeks are counted from now, not from the start of the tier. Week 1 is the next reset.
///
/// Two assumptions are baked in and worth knowing about:
/// <list type="bullet">
/// <item>Coffers come up evenly. The pool is walked round-robin rather than rolled, so a slot in a
/// pool of four shows up once every two weeks at two drops a week, which is its average rate.</item>
/// <item>Every fight is cleared every week. A group that only clears three fights finishes later
/// than the forecast, but the ranking between candidates does not change.</item>
/// </list>
/// </summary>
public sealed class WeekSimulator
{
    private readonly TierDefinition tier;
    private readonly PriorityRules rules;
    private readonly int horizon;

    private readonly List<PlannedAward> awards = new();
    private int currentWeek;
    private int currentEncounter;

    public WeekSimulator(TierDefinition tier, PriorityRules rules, int horizon)
    {
        this.tier = tier;
        this.rules = rules;
        this.horizon = Math.Max(1, horizon);
    }

    /// <summary>
    /// Runs to completion, mutating the plans it is given. Pass clones.
    ///
    /// <paramref name="startWeek"/> lets a caller hand over plans that already have a week applied
    /// to them — the coming week is decided by the full ranking rather than by the greedy rule in
    /// here, and the projection has to pick up after it rather than repeat it.
    /// </summary>
    public SimulationResult Run(IReadOnlyList<PlayerPlan> players, int startWeek = 1)
    {
        awards.Clear();

        var encounters = tier.Encounters.OrderBy(e => e.Index).ToList();

        MarkFinished(players, Math.Max(0, startWeek - 1));

        for (currentWeek = startWeek; currentWeek <= horizon && players.Any(p => !p.IsDone); currentWeek++)
        {
            foreach (var encounter in encounters)
            {
                currentEncounter = encounter.Index;

                foreach (var player in players)
                {
                    if (encounter.Index is >= 1 and <= PlayerPlan.MaxEncounters)
                        player.Tokens[encounter.Index]++;
                }

                foreach (var slot in DropsFor(tier, encounter, currentWeek))
                    AwardCoffer(players, slot);

                // One material of each kind per clear.
                foreach (var side in encounter.UpgradeDrops)
                    AwardUpgrade(players, side);
            }

            SpendTokens(players);
            MarkFinished(players, currentWeek);
        }

        var beyond = horizon + 1;
        var finishWeeks = new Dictionary<string, int>(players.Count);
        var last = 0;

        foreach (var player in players)
        {
            var week = player.FinishedWeek < 0 ? beyond : player.FinishedWeek;
            finishWeeks[player.Key] = week;
            last = Math.Max(last, week);
        }

        return new SimulationResult(last, finishWeeks, [..awards], horizon);
    }

    /// <summary>
    /// Spends what the players can already afford, without simulating a week around it.
    ///
    /// The books someone is holding came from clears that have already happened, so they can be
    /// spent before this week's fights rather than after them. Running it that way round matters for
    /// more than the shopping list: a player about to buy the body piece should not also be handed
    /// the body coffer.
    /// </summary>
    public List<PlannedAward> SpendNow(IReadOnlyList<PlayerPlan> players, int week)
    {
        awards.Clear();
        currentWeek = week;

        SpendTokens(players);

        return [..awards];
    }

    /// <summary>What a fight is expected to put up in a given week.</summary>
    public static IEnumerable<GearSlot> DropsFor(TierDefinition tier, TierEncounter encounter, int week)
    {
        var pool = encounter.DropSlots;
        if (pool.Count == 0)
            yield break;

        // Every slot, every week: no rate to average, so this is exact rather than a model.
        if (tier.AllCoffersDrop)
        {
            foreach (var slot in pool)
                yield return slot;

            yield break;
        }

        if (encounter.DropCount <= 0)
            yield break;

        // Otherwise the pool is walked round-robin. A real chest can put the same coffer up twice
        // and skip another; over the weeks that averages out to each slot appearing at
        // DropCount/pool rate, which is what this reproduces without pretending to roll dice.
        var start = (week - 1) * encounter.DropCount % pool.Count;

        for (var i = 0; i < encounter.DropCount; i++)
            yield return pool[(start + i) % pool.Count];
    }

    private void AwardCoffer(IReadOnlyList<PlayerPlan> players, GearSlot slot)
    {
        var winner = Best(players.Where(p => p.Wants(slot)));
        if (winner == null || !winner.TakeSlot(slot))
            return;

        awards.Add(new PlannedAward(currentWeek, currentEncounter, Slots.CofferSlot(slot), null,
                                    winner.Key, winner.Name, Bought: false));
    }

    private void AwardUpgrade(IReadOnlyList<PlayerPlan> players, GearSide side)
    {
        var winner = Best(players.Where(p => p.WantsUpgrade(side)));
        if (winner == null || !winner.TakeUpgrade(side, out var slot))
            return;

        awards.Add(new PlannedAward(currentWeek, currentEncounter, slot, side,
                                    winner.Key, winner.Name, Bought: false));
    }

    /// <summary>
    /// Who takes the drop — through <see cref="DropOrder"/>, the same rule the loot window and the
    /// coming week use. The simulator used to have a rule of its own here, and that is exactly how
    /// the plan and the chest came to name different people for the same coffer.
    /// </summary>
    private PlayerPlan? Best(IEnumerable<PlayerPlan> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0)
            return null;

        var ranked = DropOrder.Rank(rules, list.Select(Contend).ToList());
        return ranked.Count == 0 ? null : list.First(p => p.Key == ranked[0].Who.Key);
    }

    private static Contender Contend(PlayerPlan plan) =>
        new(plan.Key, plan.Role, plan.Order, plan.ItemsReceived, plan.Open.Count);

    /// <summary>
    /// Books are spent as soon as they cover something, on whatever is most contested — that is
    /// what a player actually does, and it is also what keeps them out of everyone else's way.
    /// </summary>
    private void SpendTokens(IReadOnlyList<PlayerPlan> players)
    {
        foreach (var player in players)
        {
            while (true)
            {
                var affordable = player.Open
                                       .Select(need => (Need: need, Cost: BookLedger.CostOf(tier, need)))
                                       .Where(x => x.Cost != null && BookLedger.CanAfford(tier, player, x.Cost))
                                       .ToList();

                if (affordable.Count == 0)
                    break;

                var choice = affordable
                             .OrderByDescending(x => Contested(players, player, x.Need))
                             .ThenByDescending(x => x.Cost!.Cost)
                             .First();

                if (!BookLedger.Pay(tier, player, choice.Cost!, out var traded))
                    break;

                player.Open.Remove(choice.Need);

                awards.Add(new PlannedAward(currentWeek, choice.Cost!.Encounter,
                                            Slots.CofferSlot(choice.Need.Slot),
                                            choice.Need.IsUpgrade ? choice.Need.Side : null,
                                            player.Key, player.Name, Bought: true, Traded: traded));
            }
        }
    }

    private static int Contested(IEnumerable<PlayerPlan> players, PlayerPlan self, OpenNeed need) =>
        players.Count(p => p != self &&
                           (need.IsUpgrade ? p.WantsUpgrade(need.Side) : p.Wants(need.Slot)));

    private static void MarkFinished(IReadOnlyList<PlayerPlan> players, int week)
    {
        foreach (var player in players)
        {
            if (player.IsDone && player.FinishedWeek < 0)
                player.FinishedWeek = week;
        }
    }
}
