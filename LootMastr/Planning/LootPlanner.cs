using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Planning;

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

    public IReadOnlyList<Candidate> RankForSlot(GearSlot slot) =>
        Rank(plan => plan.Wants(slot), plan => plan.TakeSlot(slot), slot.Label());

    public IReadOnlyList<Candidate> RankForUpgrade(GearSide side) =>
        Rank(plan => plan.WantsUpgrade(side), plan => plan.TakeUpgrade(side), $"{side} upgrade");

    private IReadOnlyList<Candidate> Rank(Func<PlayerPlan, bool> wants, Func<PlayerPlan, bool> take, string what)
    {
        var basePlans = BuildPlans();
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

            results.Add(new Candidate(
                            member, role, score, run.LastFinishWeek, ownFinish, member.ItemsReceived, saved,
                            Reason(what, run, saved, ownFinish, role, member.ItemsReceived)));
        }

        return results
               .OrderBy(c => c.Score)
               .ThenBy(c => c.ItemsReceived)
               .ThenByDescending(c => config.Rules.WeightFor(c.Role))
               .ThenBy(c => roster.Members.IndexOf(c.Member))
               .ToList();
    }

    private static string Reason(string what, SimulationResult run, int saved, int ownFinish,
                                 RaidRole role, int received)
    {
        var parts = new List<string>(4);

        parts.Add(saved > 0
                      ? $"pulls the group's last week in by {saved}"
                      : $"group still finishes W{run.LastFinishWeek}");

        parts.Add(run.BeyondHorizon(ownFinish) ? "not done inside the horizon" : $"done W{ownFinish}");

        if (role == RaidRole.Dps)
            parts.Add("damage dealer");

        parts.Add(received == 0 ? "nothing won yet" : $"{received} won so far");

        return string.Join("; ", parts);
    }

    /// <summary>The roster as simulation input. Members with nothing left are still included so the
    /// group's finish week accounts for them.</summary>
    public List<PlayerPlan> BuildPlans()
    {
        var tier = tiers.Tier;

        return roster.Members
                     .Select(m => PlayerPlan.From(m, roster.RoleOf(m), tier))
                     .ToList();
    }

    private static List<PlayerPlan> Clone(IEnumerable<PlayerPlan> plans) =>
        plans.Select(p => p.Clone()).ToList();

    private WeekSimulator NewSimulator() =>
        new(tiers.Tier, config.Rules, config.LookaheadWeeks);
}
