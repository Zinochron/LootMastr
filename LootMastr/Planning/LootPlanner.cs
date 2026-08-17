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
    private readonly GearComparer? gear;

    /// <summary>
    /// <paramref name="gear"/> is optional, and its absence is the normal case for a group that has
    /// never read anyone's equipment. Without it there are no damage gains and the rules fall back to
    /// counting items, which is what they did before any of this existed.
    /// </summary>
    public LootPlanner(Configuration config, TierCatalog tiers, RosterStore roster,
                       GearComparer? gear = null)
    {
        this.config = config;
        this.tiers = tiers;
        this.roster = roster;
        this.gear = gear;
    }

    /// <summary>Whether a damage-based ranking can say anything at all right now.</summary>
    public bool CanRankByDamage =>
        gear != null && roster.Members.Any(m => m.HasMeasuredStats);

    /// <summary>
    /// Just the coming week: every coffer that can turn up, and what the books in hand already buy.
    ///
    /// Worked out here rather than inside the simulator so that each award can carry the ranking
    /// that produced it — the plan used to show a winner from one calculation beside runners-up
    /// from another, which is exactly how a table comes to contradict itself.
    /// </summary>
    public SimulationResult ComingWeek()
    {
        var plans = BuildPlans();
        var pending = new List<PendingAward>();

        // Books first. They were earned in weeks that have already happened, so they are spendable
        // before this week's fights — and something bought is something that no longer needs to be
        // won, which the drops below have to know about.
        var bought = NewSimulator().SpendNow(plans, 1);

        foreach (var award in bought)
            pending.Add(new PendingAward(award.PlayerKey, award.Slot, award.Upgrade));

        var drops = AssignComingWeek(plans, pending);

        return new SimulationResult(0, new Dictionary<string, int>(), [..bought, ..drops],
                                    config.LookaheadWeeks);
    }

    /// <summary>
    /// The whole tier played forward from here: who gets what, in which week, drop or purchase.
    ///
    /// A run of its own rather than "the coming week, then the rest". Splitting it that way cost a
    /// week of books — the coming week was handled outside the simulator, which is the only thing
    /// that hands books out, so nobody earned anything in week 1 and every purchase in the plan sat
    /// a week later than it should. Week 1 here means one book from every fight, week 2 two, on top
    /// of whatever each player is already holding.
    ///
    /// It agrees with <see cref="ComingWeek"/> about week 1's drops without being told to, because
    /// both hand the same drop pool to the same rule.
    /// </summary>
    public SimulationResult Schedule() => NewSimulator().Run(BuildPlans());

    /// <summary>
    /// Hands out the coming week's drops by ranking, applying each to the plans before deciding the
    /// next — the same order-of-decision the loot window uses, so a chest holding two of a kind does
    /// not name the same player twice.
    ///
    /// Every coffer a fight can put up is listed, not the number the tier says drops. A drop count
    /// is a rate for the projection to average over; it cannot say <em>which</em> two of the four
    /// accessories turn up on Friday, and answering "the first two in the pool" made the table both
    /// wrong and useless — the point of it is to know who each coffer is for before it drops.
    /// </summary>
    private List<PlannedAward> AssignComingWeek(List<PlayerPlan> plans, List<PendingAward> pending)
    {
        var tier = tiers.Tier;
        var awards = new List<PlannedAward>();

        foreach (var encounter in tier.Encounters.OrderBy(e => e.Index))
        {
            foreach (var slot in encounter.DropSlots)
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

                winner.TakeUpgrade(side, out var slot);
                pending.Add(new PendingAward(winner.Key, null, side));

                awards.Add(new PlannedAward(1, encounter.Index, slot, side,
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
        Rank(plan => plan.Wants(slot), plan => plan.GainFor(slot), applied);

    public IReadOnlyList<Candidate> RankForUpgrade(GearSide side, IEnumerable<PendingAward>? applied = null) =>
        Rank(plan => plan.WantsUpgrade(side), plan => plan.GainForUpgrade(side), applied);

    /// <summary>
    /// Everyone who still needs this drop, in the group's own order.
    ///
    /// One projection is run, shared by the whole ranking, and it decides nothing — it only says how
    /// long each candidate is currently waiting, which is worth seeing beside a queue that might be
    /// making one of them wait a long time. The previous version ran a full simulation <i>per
    /// candidate</i> and ordered by the result, which was both slower and impossible to explain.
    /// </summary>
    private IReadOnlyList<Candidate> Rank(Func<PlayerPlan, bool> wants, Func<PlayerPlan, double> gainOf,
                                          IEnumerable<PendingAward>? applied)
    {
        var basePlans = BuildPlans(applied);
        var eligible = basePlans.Where(wants).ToList();
        if (eligible.Count == 0)
            return [];

        var baseline = NewSimulator().Run(Clone(basePlans));

        var contenders = eligible
                         .Select(p => new Contender(p.Key, p.Role, p.Order, p.ItemsReceived, p.Open.Count,
                                                    gainOf(p)))
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
    /// What this player should spend their books on this week, straight out of the plan.
    ///
    /// It used to list everything they <em>could</em> afford, which for anyone mid-tier is most of
    /// their remaining list and reads as a shopping list rather than an instruction. Two of those
    /// entries are usually alternatives — buying one puts the other out of reach — and nothing on
    /// screen said which. The plan already decides, so this reports that decision and stops.
    /// </summary>
    public IReadOnlyList<string> ShouldBuyNow(SimulationResult forecast, RosterMember member)
    {
        var tier = tiers.Tier;
        var result = new List<string>();

        foreach (var award in forecast.Awards)
        {
            if (!award.Bought || award.Week != 1 || award.PlayerKey != member.Key)
                continue;

            var cost = award.Upgrade != null
                           ? tier.CostForUpgrade(award.Upgrade.Value)
                           : award.Slot != null
                               ? tier.CostForSlot(award.Slot.Value)
                               : null;

            if (cost == null)
            {
                result.Add(award.What);
                continue;
            }

            var fight = tier.Encounter(cost.Encounter)?.Name ?? $"#{cost.Encounter}";
            var text = $"{award.What} for {cost.Cost} {fight} book(s)";

            // Say so when part of it has to be traded for first, with the figure the purchase
            // actually used rather than one worked out again from the counts.
            if (award.Traded is { } trade)
            {
                var source = tier.Encounter(trade.FromEncounter)?.Name ?? $"#{trade.FromEncounter}";
                text += $" (exchange {trade.Books} × {source})";
            }

            result.Add(text);
        }

        return result;
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

        if (config.Rules.Basis != NeedBasis.MissingGear)
            FillGains(plans);

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

    /// <summary>
    /// What each open need would be worth to each player, in damage percent.
    ///
    /// Measured once here rather than inside the simulator's inner loop, and held constant for the
    /// run. Taking the body piece does slightly change what the legs would be worth, through the
    /// stat totals — that is second order, and the alternative is a damage model running thousands
    /// of times per frame.
    ///
    /// A material's worth is the best of the pieces it could go into: handing somebody a twine lets
    /// them upgrade whichever one helps most, so that is the number to rank them by.
    /// </summary>
    private void FillGains(IReadOnlyList<PlayerPlan> plans)
    {
        if (gear == null)
            return;

        foreach (var plan in plans)
        {
            var member = roster.Members.FirstOrDefault(m => m.Key == plan.Key);
            if (member == null || !member.HasMeasuredStats)
                continue;

            foreach (var need in plan.Open)
            {
                var target = member.NeedFor(need.Slot).BisItemId;
                if (target == 0)
                    continue;

                if (gear.Gain(member, need.Slot, target) is not { } change)
                    continue;

                if (need.IsUpgrade)
                {
                    var best = plan.UpgradeGains.GetValueOrDefault(need.Side);
                    plan.UpgradeGains[need.Side] = Math.Max(best, change.Percent);
                }
                else
                {
                    plan.SlotGains[Slots.CofferSlot(need.Slot)] = change.Percent;
                }
            }
        }
    }

    private static List<PlayerPlan> Clone(IEnumerable<PlayerPlan> plans) =>
        plans.Select(p => p.Clone()).ToList();

    private WeekSimulator NewSimulator() =>
        new(tiers.Tier, config.Rules, config.LookaheadWeeks);
}
