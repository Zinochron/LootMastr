using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Planning;

/// <summary>An item earlier in the same chest that is already spoken for.</summary>
public readonly record struct PendingAward(string PlayerKey, GearSlot? Slot, GearSide? Upgrade);

/// <summary>One player weighed up for one drop, with the numbers that put them where they are.</summary>
public sealed record Candidate(
    RosterMember Member,
    RaidRole Role,
    double Score,
    int GroupFinish,
    int OwnFinish,
    int ItemsReceived,
    int WeeksSaved,
    string Reason);

/// <summary>
/// Turns the roster into an answer to "who should get this". Every candidate is judged by playing
/// the rest of the tier forward with that player holding the item, so the ranking is about what the
/// assignment does to the group's finish date rather than about who shouted first.
/// </summary>
public sealed class LootPlanner
{
    private readonly Configuration config;
    private readonly TierCatalog tiers;
    private readonly RosterStore roster;

    public LootPlanner(Configuration config, TierCatalog tiers, RosterStore roster)
    {
        this.config = config;
        this.tiers = tiers;
        this.roster = roster;
    }

    /// <summary>The forecast as things stand, with nothing handed out yet.</summary>
    public SimulationResult Forecast() => NewSimulator().Run(BuildPlans());

    public IReadOnlyList<Candidate> RankForSlot(GearSlot slot, IEnumerable<PendingAward>? applied = null) =>
        Rank(plan => plan.Wants(slot), plan => plan.TakeSlot(slot), slot.CofferLabel(), applied);

    public IReadOnlyList<Candidate> RankForUpgrade(GearSide side, IEnumerable<PendingAward>? applied = null) =>
        Rank(plan => plan.WantsUpgrade(side), plan => plan.TakeUpgrade(side), $"{side} upgrade", applied);

    private IReadOnlyList<Candidate> Rank(Func<PlayerPlan, bool> wants, Func<PlayerPlan, bool> take, string what,
                                          IEnumerable<PendingAward>? applied = null)
    {
        var basePlans = BuildPlans(applied);
        var eligible = basePlans.Where(wants).Select(p => p.Key).ToList();
        if (eligible.Count == 0)
            return [];

        var simulator = NewSimulator();
        var baseline = simulator.Run(Clone(basePlans));
        var baselineGroup = baseline.LastFinishWeek;

        var results = new List<Candidate>(eligible.Count);

        foreach (var key in eligible)
        {
            var plans = Clone(basePlans);
            var self = plans.First(p => p.Key == key);
            take(self);

            var run = NewSimulator().Run(plans);
            var member = roster.Members.First(m => m.Key == key);
            var role = roster.RoleOf(member);

            var ownFinish = run.FinishWeeks.GetValueOrDefault(key, run.Horizon + 1);
            var saved = baselineGroup - run.LastFinishWeek;

            // The fairness term is deliberately tiny: it settles ties without ever outweighing a
            // real week. Received items push the score up, so fewer is better.
            var score = NewSimulator().Score(run, plans.Count) +
                        (member.ItemsReceived * config.Rules.FairnessWeight);

            var ownBefore = baseline.FinishWeeks.GetValueOrDefault(key, baseline.Horizon + 1);

            results.Add(new Candidate(
                            member, role, score, run.LastFinishWeek, ownFinish, member.ItemsReceived, saved,
                            Reason(run, saved, ownFinish, ownBefore, role, member.ItemsReceived)));
        }

        return results
               .OrderBy(c => c.Score)
               .ThenBy(c => c.ItemsReceived)
               .ThenByDescending(c => config.Rules.WeightFor(c.Role))
               .ThenBy(c => roster.Members.IndexOf(c.Member))
               .ToList();
    }

    private static string Reason(SimulationResult run, int saved, int ownFinish, int ownBefore,
                                 RaidRole role, int received)
    {
        var parts = new List<string>(4);

        parts.Add(saved > 0
                      ? $"pulls the group's last week in by {saved}"
                      : $"group still finishes W{run.LastFinishWeek}");

        // What it does for them, not just where they end up. When the group's last week is the same
        // whoever takes it — which it is whenever "weight on the slowest player" is turned down —
        // this is the whole of why one candidate beat another, and it used to be missing.
        var own = run.BeyondHorizon(ownFinish) ? "not done inside the horizon" : $"done W{ownFinish}";

        if (ownBefore > ownFinish)
            own += $", was W{(run.BeyondHorizon(ownBefore) ? run.Horizon + 1 : ownBefore)}";

        parts.Add(own);
        parts.Add(role == RaidRole.Dps ? "damage dealer" : role.ToString().ToLowerInvariant());
        parts.Add(received == 0 ? "nothing won yet" : $"{received} won so far");

        return string.Join("; ", parts);
    }

    /// <summary>
    /// What this player's books would buy right now, cheapest first. Answers the question the book
    /// counts are actually for: whether someone needs to compete for a coffer at all.
    /// </summary>
    public IReadOnlyList<string> AffordableNow(RosterMember member)
    {
        var tier = tiers.Tier;
        var plan = PlayerPlan.From(member, roster.RoleOf(member), tier);
        var result = new List<(int Cost, string Text)>();

        foreach (var need in plan.Open)
        {
            var cost = BookLedger.CostOf(tier, need);
            if (cost == null || !BookLedger.CanAfford(tier, plan, cost))
                continue;

            var fight = tier.Encounter(cost.Encounter)?.Name ?? $"#{cost.Encounter}";
            var text = $"{need.Describe()} for {cost.Cost} {fight} book(s)";

            // Say so when it only works by trading books in — it is not obvious from the counts.
            if (plan.Tokens[Math.Clamp(cost.Encounter, 0, PlayerPlan.MaxEncounters)] < cost.Cost)
            {
                var source = tier.ConvertibleSourceFor(cost.Encounter);
                if (source != null)
                    text += $" (trading in {tier.Encounter(source.Value)?.Name ?? "later"} books)";
            }

            result.Add((cost.Cost, text));
        }

        return result.OrderBy(r => r.Cost).Select(r => r.Text).ToList();
    }

    /// <summary>
    /// The roster as simulation input. Members with nothing left are still included so the group's
    /// finish week accounts for them.
    ///
    /// <paramref name="applied"/> is for items already spoken for but not yet handed over — the
    /// rest of an open chest. A chest can hold the same coffer twice, and since the gear is unique
    /// the second one cannot go to whoever is taking the first.
    /// </summary>
    public List<PlayerPlan> BuildPlans(IEnumerable<PendingAward>? applied = null)
    {
        var tier = tiers.Tier;

        var plans = roster.Members
                          .Select(m => PlayerPlan.From(m, roster.RoleOf(m), tier))
                          .ToList();

        if (applied == null)
            return plans;

        foreach (var award in applied)
        {
            var plan = plans.FirstOrDefault(p => p.Key == award.PlayerKey);
            if (plan == null)
                continue;

            if (award.Upgrade != null)
                plan.TakeUpgrade(award.Upgrade.Value);
            else if (award.Slot != null)
                plan.TakeSlot(award.Slot.Value);
        }

        return plans;
    }

    private static List<PlayerPlan> Clone(IEnumerable<PlayerPlan> plans) =>
        plans.Select(p => p.Clone()).ToList();

    private WeekSimulator NewSimulator() =>
        new(tiers.Tier, config.Rules, config.LookaheadWeeks);
}
