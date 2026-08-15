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
    bool Bought)
{
    public string What => Upgrade != null ? $"{Upgrade} upgrade" : Slot?.Label() ?? "?";
}

public sealed record SimulationResult(
    int LastFinishWeek,
    double WeightedFinish,
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

    /// <summary>Runs to completion, mutating the plans it is given. Pass clones.</summary>
    public SimulationResult Run(IReadOnlyList<PlayerPlan> players)
    {
        awards.Clear();

        var encounters = tier.Encounters.OrderBy(e => e.Index).ToList();

        MarkFinished(players, 0);

        for (currentWeek = 1; currentWeek <= horizon && players.Any(p => !p.IsDone); currentWeek++)
        {
            foreach (var encounter in encounters)
            {
                currentEncounter = encounter.Index;

                foreach (var player in players)
                {
                    if (encounter.Index is >= 1 and <= PlayerPlan.MaxEncounters)
                        player.Tokens[encounter.Index]++;
                }

                foreach (var slot in DropsFor(encounter, currentWeek))
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
        var weighted = 0d;
        var last = 0;

        foreach (var player in players)
        {
            var week = player.FinishedWeek < 0 ? beyond : player.FinishedWeek;
            finishWeeks[player.Key] = week;
            weighted += rules.WeightFor(player.Role) * week;
            last = Math.Max(last, week);
        }

        return new SimulationResult(last, weighted, finishWeeks, [..awards], horizon);
    }

    /// <summary>
    /// A single number to compare two candidate assignments by. The last finisher dominates, with
    /// the weighted average breaking ties — that is what lets a damage dealer win a coin flip
    /// without ever letting the group as a whole finish later.
    /// </summary>
    public double Score(SimulationResult result, int playerCount)
    {
        var average = playerCount == 0 ? 0 : result.WeightedFinish / playerCount;
        return (rules.LastFinisherWeight * result.LastFinishWeek) + average;
    }

    private static IEnumerable<GearSlot> DropsFor(TierEncounter encounter, int week)
    {
        var pool = encounter.DropSlots;
        if (pool.Count == 0 || encounter.DropCount <= 0)
            yield break;

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
        if (winner == null || !winner.TakeUpgrade(side))
            return;

        awards.Add(new PlannedAward(currentWeek, currentEncounter, null, side,
                                    winner.Key, winner.Name, Bought: false));
    }

    /// <summary>
    /// Inside the simulation, the drop goes to whoever is furthest from done. Handing it to anyone
    /// else can only push the last finisher out, which is the thing being minimised.
    /// </summary>
    private PlayerPlan? Best(IEnumerable<PlayerPlan> candidates) =>
        candidates.OrderByDescending(p => p.Open.Count)
                  .ThenByDescending(p => rules.WeightFor(p.Role))
                  .ThenBy(p => p.ItemsReceived)
                  .ThenBy(p => p.Key, StringComparer.Ordinal)
                  .FirstOrDefault();

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

                if (!BookLedger.Pay(tier, player, choice.Cost!))
                    break;

                player.Open.Remove(choice.Need);

                awards.Add(new PlannedAward(currentWeek, choice.Cost!.Encounter,
                                            choice.Need.IsUpgrade ? null : Slots.CofferSlot(choice.Need.Slot),
                                            choice.Need.IsUpgrade ? choice.Need.Side : null,
                                            player.Key, player.Name, Bought: true));
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
