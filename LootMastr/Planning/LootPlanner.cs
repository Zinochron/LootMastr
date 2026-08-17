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
    int Order,
    int ItemsReceived,
    int OpenNeeds,
    int FinishWeek,
    string Reason);

/// <summary>
/// Turns the roster into an answer to "who should get this", by the group's own rule — see
/// <see cref="DropOrder"/>. The simulator is still run, but only to say when each player would be
/// finished; it no longer picks anybody.
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

    /// <summary>
    /// The forecast as things stand.
    ///
    /// The coming week is worked out here rather than inside the simulator, so that each award can
    /// carry the ranking that produced it — the plan used to show a winner from one calculation
    /// beside runners-up from another, which is exactly how a table comes to contradict itself. The
    /// weeks after it are projected by the simulator, which now applies the same rule.
    /// </summary>
    public SimulationResult Forecast()
    {
        var plans = BuildPlans();
        var thisWeek = AssignComingWeek(plans);

        var rest = NewSimulator().Run(plans, startWeek: 2);

        return rest with { Awards = [..thisWeek, ..rest.Awards] };
    }

    /// <summary>
    /// Hands out the coming week's expected drops by ranking, applying each to the plans before
    /// deciding the next — the same order-of-decision the loot window uses, so a chest holding two
    /// of a kind does not name the same player twice.
    /// </summary>
    private List<PlannedAward> AssignComingWeek(List<PlayerPlan> plans)
    {
        var tier = tiers.Tier;
        var awards = new List<PlannedAward>();
        var pending = new List<PendingAward>();

        foreach (var encounter in tier.Encounters.OrderBy(e => e.Index))
        {
            foreach (var slot in WeekSimulator.DropsFor(tier, encounter, 1))
            {
                var coffer = Slots.CofferSlot(slot);
                var ranking = RankForSlot(slot, pending);

                var winner = WinnerOf(plans, ranking);
                if (winner == null)
                    continue;

                winner.TakeSlot(slot);
                pending.Add(new PendingAward(winner.Key, coffer, null));

                awards.Add(new PlannedAward(1, encounter.Index, coffer, null,
                                            winner.Key, winner.Name, Bought: false,
                                            Considered: Considered(ranking), Why: ranking[0].Reason));
            }

            foreach (var side in encounter.UpgradeDrops)
            {
                var ranking = RankForUpgrade(side, pending);

                var winner = WinnerOf(plans, ranking);
                if (winner == null)
                    continue;

                winner.TakeUpgrade(side);
                pending.Add(new PendingAward(winner.Key, null, side));

                awards.Add(new PlannedAward(1, encounter.Index, null, side,
                                            winner.Key, winner.Name, Bought: false,
                                            Considered: Considered(ranking), Why: ranking[0].Reason));
            }
        }

        return awards;
    }

    /// <summary>
    /// The ranking as the award carries it. Names and reasons only — the table shows exactly the
    /// list the winner came off, rather than a fresh one computed without the earlier drops of the
    /// same week applied, which is what used to make the two disagree.
    /// </summary>
    private static List<AwardCandidate> Considered(IReadOnlyList<Candidate> ranking) =>
        ranking.Select(c => new AwardCandidate(c.Member.Name, c.Reason)).ToList();

    private static PlayerPlan? WinnerOf(List<PlayerPlan> plans, IReadOnlyList<Candidate> ranking) =>
        ranking.Count == 0 ? null : plans.FirstOrDefault(p => p.Key == ranking[0].Member.Key);

    public IReadOnlyList<Candidate> RankForSlot(GearSlot slot, IEnumerable<PendingAward>? applied = null) =>
        Rank(plan => plan.Wants(slot), applied);

    public IReadOnlyList<Candidate> RankForUpgrade(GearSide side, IEnumerable<PendingAward>? applied = null) =>
        Rank(plan => plan.WantsUpgrade(side), applied);

    /// <summary>
    /// Everyone who still needs this drop, in the group's own order.
    ///
    /// One projection is run, shared by the whole ranking, and it decides nothing — it only says how
    /// long each candidate is currently waiting, which is worth seeing beside a queue that might be
    /// making one of them wait a long time. The previous version ran a full simulation <i>per
    /// candidate</i> and ordered by the result, which was both slower and impossible to explain.
    /// </summary>
    private IReadOnlyList<Candidate> Rank(Func<PlayerPlan, bool> wants, IEnumerable<PendingAward>? applied)
    {
        var basePlans = BuildPlans(applied);
        var eligible = basePlans.Where(wants).ToList();
        if (eligible.Count == 0)
            return [];

        var baseline = NewSimulator().Run(Clone(basePlans));

        var contenders = eligible
                         .Select(p => new Contender(p.Key, p.Role, p.Order, p.ItemsReceived, p.Open.Count))
                         .ToList();

        var results = new List<Candidate>(eligible.Count);

        foreach (var placing in DropOrder.Rank(config.Rules, contenders))
        {
            var member = roster.Members.FirstOrDefault(m => m.Key == placing.Who.Key);
            if (member == null)
                continue;

            var finish = baseline.FinishWeeks.GetValueOrDefault(placing.Who.Key, baseline.Horizon + 1);

            var waiting = baseline.BeyondHorizon(finish)
                              ? $"not done inside {baseline.Horizon} weeks as things stand"
                              : $"on track for W{finish}";

            results.Add(new Candidate(
                            member, placing.Who.Role, placing.Who.Order, placing.Who.ItemsReceived,
                            placing.Who.OpenNeeds, finish,
                            $"{DropOrder.Explain(config.Rules, placing)}; {waiting}"));
        }

        return results;
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

        // The index is the player order, so the rules can be told to follow it.
        var plans = roster.Members
                          .Select((m, i) => PlayerPlan.From(m, roster.RoleOf(m), tier, i))
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
