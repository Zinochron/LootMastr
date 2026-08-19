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
    BookTrade? Traded = null,
    int TomeCost = 0,
    bool Manual = false)
{
    /// <summary>Whether this came from a pin rather than from the rules.</summary>
    public bool ByHand => Manual;

    /// <summary>Bought from the tomestone vendor rather than won or traded for with books.</summary>
    public bool WithTomestones => TomeCost > 0;

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

/// <summary>
/// What the projection came out with. Two clocks, on purpose.
///
/// <see cref="FinishWeeks"/> is when the raid stops owing somebody anything - coffers and materials.
/// <see cref="TomeFinishWeeks"/> is when their set is actually finished, tomestone pieces included.
/// The second is never earlier than the first and is frequently several weeks later, because 450 a
/// week is the one rate in this tier that clearing faster does not change.
/// </summary>
public sealed record SimulationResult(
    int LastFinishWeek,
    IReadOnlyDictionary<string, int> FinishWeeks,
    IReadOnlyList<PlannedAward> Awards,
    int Horizon,
    int LastTomeFinishWeek = 0,
    IReadOnlyDictionary<string, int>? TomeFinishWeeks = null,
    IReadOnlyList<PlanProblem>? Problems = null)
{
    /// <summary>Pins the run could not honour. Empty unless somebody has set something by hand.</summary>
    public IReadOnlyList<PlanProblem> Trouble => Problems ?? [];

    public IEnumerable<PlanProblem> TroubleIn(int week) => Trouble.Where(p => p.Week == week);

    /// <summary>True for a week the simulation never reached, i.e. "not within the horizon".</summary>
    public bool BeyondHorizon(int week) => week > Horizon;

    /// <summary>When this player's whole set is done, raid and vendor both.</summary>
    public int WholeSetWeek(string key) =>
        TomeFinishWeeks?.GetValueOrDefault(key, Horizon + 1) ?? FinishWeeks.GetValueOrDefault(key, Horizon + 1);
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
    private readonly ManualPlan manual;

    private readonly List<PlannedAward> awards = new();
    private readonly List<PlanProblem> problems = new();
    private int currentWeek;
    private int currentEncounter;

    /// <summary>How many of each coffer this fight has put up so far this week, for the ordinal.</summary>
    private readonly Dictionary<GearSlot, int> seenThisFight = new();

    public WeekSimulator(TierDefinition tier, PriorityRules rules, int horizon, ManualPlan? manual = null)
    {
        this.tier = tier;
        this.rules = rules;
        this.horizon = Math.Max(1, horizon);
        this.manual = manual ?? new ManualPlan();
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
        problems.Clear();

        var encounters = tier.Encounters.OrderBy(e => e.Index).ToList();

        MarkFinished(players, Math.Max(0, startWeek - 1));

        for (currentWeek = startWeek; currentWeek <= horizon && players.Any(p => !p.IsFullyDone); currentWeek++)
        {
            // Tomestones arrive once a week whatever happens, which is the whole reason they are the
            // slower clock: a group that clears twice as fast still waits the same number of weeks
            // for a body piece. Books are earned per clear, a few lines further down.
            foreach (var player in players)
                player.Tomes += tier.TomestonesPerWeek;

            foreach (var encounter in encounters)
            {
                currentEncounter = encounter.Index;
                seenThisFight.Clear();

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

            // Pinned purchases before the greedy ones, or a player's books would already be spent on
            // whatever the rules preferred by the time the group's own decision was read.
            SpendPinned(players);
            SpendTokens(players);
            SpendTomes(players);
            MarkFinished(players, currentWeek);
        }

        var beyond = horizon + 1;
        var finishWeeks = new Dictionary<string, int>(players.Count);
        var tomeWeeks = new Dictionary<string, int>(players.Count);
        var last = 0;
        var lastTome = 0;

        foreach (var player in players)
        {
            var week = player.FinishedWeek < 0 ? beyond : player.FinishedWeek;
            finishWeeks[player.Key] = week;
            last = Math.Max(last, week);

            // The whole set is done when both halves are, so this is the later of the two rather
            // than the tomestone half alone - a player still owed a coffer is not finished.
            var tome = Math.Max(week, player.TomeFinishedWeek < 0 ? beyond : player.TomeFinishedWeek);
            tomeWeeks[player.Key] = tome;
            lastTome = Math.Max(lastTome, tome);
        }

        return new SimulationResult(last, finishWeeks, [..awards], horizon, lastTome, tomeWeeks,
                                    [..problems]);
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
        var coffer = Slots.CofferSlot(slot);
        var ordinal = Next(coffer);

        PlayerPlan? winner;

        if (manual.For(currentWeek, currentEncounter, coffer, null, ordinal) is { } pin)
        {
            winner = Pinned(players, pin, p => p.Wants(slot), coffer.CofferLabel());

            if (winner == null)
                return;
        }
        else
        {
            winner = Best(players.Where(p => p.Wants(slot)), p => p.GainFor(slot));
        }

        if (winner == null || !winner.TakeSlot(slot))
            return;

        awards.Add(new PlannedAward(currentWeek, currentEncounter, coffer, null,
                                    winner.Key, winner.Name, Bought: false));
    }

    private void AwardUpgrade(IReadOnlyList<PlayerPlan> players, GearSide side)
    {
        // No ordinal for a material: a fight drops at most one of each side, so the side alone
        // names it. Counting them would invent a distinction the game does not make.
        PlayerPlan? winner;

        if (manual.For(currentWeek, currentEncounter, null, side, 0) is { } pin)
        {
            winner = Pinned(players, pin, p => p.WantsUpgrade(side), $"{side} upgrade");

            if (winner == null)
                return;

            // The rules skip somebody who cannot use a material yet. A pin overrules that, and has
            // to say what it is overruling rather than quietly being the better judge.
            if (!winner.CanUseUpgrade(side))
            {
                problems.Add(new PlanProblem(currentWeek, $"{side} upgrade",
                                             $"{winner.Name} has nothing to use it on yet — the piece " +
                                             "it goes into is not bought."));
            }
        }
        else
        {
            winner = Best(Usable(Field(rules, players.Where(p => p.WantsUpgrade(side))), side),
                          p => p.GainForUpgrade(side));
        }

        if (winner == null || !winner.TakeUpgrade(side, out var slot))
            return;

        awards.Add(new PlannedAward(currentWeek, currentEncounter, slot, side,
                                    winner.Key, winner.Name, Bought: false));
    }

    /// <summary>How many of this coffer the current fight has put up already. Zero almost always.</summary>
    private int Next(GearSlot coffer)
    {
        var seen = seenThisFight.GetValueOrDefault(coffer);
        seenThisFight[coffer] = seen + 1;

        return seen;
    }

    /// <summary>
    /// Who a pin names, or null when it names nobody usable — and why, when the answer is a mistake.
    ///
    /// Two of the three ways this returns null are somebody's error and are reported: a player who
    /// has left the static, and one who does not want the thing. The third, a pin that deliberately
    /// says nobody, passes in silence because it is an answer rather than a problem.
    /// </summary>
    private PlayerPlan? Pinned(IReadOnlyList<PlayerPlan> players, ManualAward pin,
                               Func<PlayerPlan, bool> wants, string what)
    {
        if (pin.IsNobody)
            return null;

        var player = players.FirstOrDefault(p => p.Key == pin.PlayerKey);

        if (player == null)
        {
            problems.Add(new PlanProblem(currentWeek, what,
                                         "Set by hand for somebody who is no longer in this static."));
            return null;
        }

        if (!wants(player))
        {
            problems.Add(new PlanProblem(currentWeek, what,
                                         $"Set by hand for {player.Name}, who does not need it — " +
                                         "already has it, or never planned it."));
            return null;
        }

        return player;
    }

    /// <summary>
    /// Who takes the drop — through <see cref="DropOrder"/>, the same rule the loot window and the
    /// coming week use. The simulator used to have a rule of its own here, and that is exactly how
    /// the plan and the chest came to name different people for the same coffer.
    /// </summary>
    private PlayerPlan? Best(IEnumerable<PlayerPlan> candidates, Func<PlayerPlan, double> gain)
    {
        var list = Field(rules, candidates).ToList();
        if (list.Count == 0)
            return null;

        // The gain is per drop, so it is handed in rather than read off the plan: what a body coffer
        // is worth and what a ring is worth are different numbers for the same player.
        var contenders = list.Select(p => Contend(p, gain(p))).ToList();

        var ranked = DropOrder.Rank(rules, contenders);
        return ranked.Count == 0 ? null : list.First(p => p.Key == ranked[0].Who.Key);
    }

    /// <summary>
    /// Drops alt characters out of a field, unless there is nobody else and the rules allow it.
    ///
    /// A second character exists to make a fight clearable twice, so gear landing on one is gear
    /// that left the raid. That makes this a gate and not a weight: an alt is never ranked against a
    /// main and loses, it is simply not in the field while any main wants the thing.
    ///
    /// Applied in the chest and in the projection from here, for the same reason every other rule
    /// is — two copies would eventually name two different people for one coffer.
    /// </summary>
    public static IEnumerable<PlayerPlan> Field(PriorityRules rules, IEnumerable<PlayerPlan> candidates)
    {
        var list = candidates.ToList();
        var mains = list.Where(p => !p.IsAlt).ToList();

        if (mains.Count > 0)
            return mains;

        return rules.AltsMayTakeSpareGear ? list : [];
    }

    /// <summary>
    /// Narrows a material's field to whoever could actually use it this week, and gives up quietly
    /// when nobody can.
    ///
    /// A twine in the bag of somebody who cannot buy the body piece for another four weeks is four
    /// weeks the group did not have to lose. But refusing to hand it over at all would be worse -
    /// the chest keeps it and nobody gets it ever. So this is a preference, not a veto: if not one
    /// candidate can use it, the normal order decides and somebody holds it.
    /// </summary>
    public static IEnumerable<PlayerPlan> Usable(IEnumerable<PlayerPlan> candidates, GearSide side)
    {
        var list = candidates.ToList();
        var usable = list.Where(p => p.CanUseUpgrade(side)).ToList();

        return usable.Count > 0 ? usable : list;
    }

    /// <summary>
    /// Tomestone pieces, bought as soon as they are affordable.
    ///
    /// The order is what a player actually does. A piece whose material is already sitting in the
    /// bag comes first, because buying it finishes a slot the same evening; then the ones still
    /// waiting on a drop; then plain tomestone gear. Within each, the expensive piece first - it is
    /// the bigger upgrade, and with a flat weekly income the last purchase lands in the same week
    /// either way.
    /// </summary>
    private void SpendTomes(IReadOnlyList<PlayerPlan> players)
    {
        foreach (var player in players)
        {
            while (true)
            {
                var affordable = player.TomeOpen
                                       .Where(n => !ReservedElsewhere(player.Key, n.Slot, null))
                                       .Where(n => TomeLedger.CanAfford(player, n.Cost))
                                       .ToList();

                if (affordable.Count == 0)
                    break;

                var choice = affordable
                             .OrderByDescending(n => Waiting(player, n))
                             .ThenByDescending(n => n.Cost)
                             .First();

                if (!TomeLedger.Pay(player, choice.Cost))
                    break;

                player.TakeTome(choice.Slot);

                // Encounter 0: no fight hands this over. The week view files purchases at the end of
                // the week rather than under a fight, for exactly that reason.
                awards.Add(new PlannedAward(currentWeek, 0, Slots.CofferSlot(choice.Slot), null,
                                            player.Key, player.Name, Bought: true, TomeCost: choice.Cost));
            }
        }
    }

    /// <summary>How badly a purchase is wanted: 2 the material is in hand, 1 it is coming, 0 neither.</summary>
    private static int Waiting(PlayerPlan player, TomeNeed need)
    {
        if (!need.ForAugment)
            return 0;

        return player.Open.Any(o => o.IsUpgrade && o.Slot == need.Slot) ? 1 : 2;
    }

    private static Contender Contend(PlayerPlan plan, double gain) =>
        new(plan.Key, plan.Role, plan.Order, plan.ItemsReceived, plan.Open.Count, gain);

    /// <summary>
    /// Books are spent as soon as they cover something, on whatever is most contested — that is
    /// what a player actually does, and it is also what keeps them out of everyone else's way.
    /// </summary>
    /// <summary>
    /// Purchases somebody wrote down, carried out before the rules get to spend anything.
    ///
    /// Order is the whole point. The greedy pass buys whatever is most contested, and it is thorough
    /// — run first, it would have spent the books a pinned purchase needed and the pin would fail
    /// for a reason nobody could see from the plan.
    /// </summary>
    private void SpendPinned(IReadOnlyList<PlayerPlan> players)
    {
        foreach (var pin in manual.PurchasesIn(currentWeek))
        {
            var what = pin.Upgrade != null
                           ? $"{pin.Upgrade} upgrade"
                           : pin.Slot?.CofferLabel() ?? "purchase";

            if (pin.IsNobody)
                continue;

            var player = players.FirstOrDefault(p => p.Key == pin.PlayerKey);

            if (player == null)
            {
                problems.Add(new PlanProblem(currentWeek, what,
                                             "Bought by hand for somebody who is no longer in this static."));
                continue;
            }

            if (pin.WithTomestones)
            {
                BuyPinnedWithTomes(player, pin, what);
                continue;
            }

            BuyPinnedWithBooks(player, pin, what);
        }
    }

    private void BuyPinnedWithTomes(PlayerPlan player, ManualAward pin, string what)
    {
        var slot = pin.Slot;

        if (slot == null)
        {
            problems.Add(new PlanProblem(currentWeek, what, "A tomestone purchase needs a slot."));
            return;
        }

        // Found by index rather than by FirstOrDefault. TomeNeed is a struct, so "not found" comes
        // back as a zeroed one, and telling that apart from a real row means trusting that no real
        // row is ever all zeroes — which is true today and is not a thing worth depending on.
        var at = player.TomeOpen.FindIndex(n => Slots.CofferSlot(n.Slot) == Slots.CofferSlot(slot.Value));

        if (at < 0)
        {
            problems.Add(new PlanProblem(currentWeek, what,
                                         $"{player.Name} has nothing to buy there — it is not " +
                                         "tomestone gear in their set, or it is already bought."));
            return;
        }

        var owed = player.TomeOpen[at];

        if (!TomeLedger.Pay(player, owed.Cost))
        {
            // The shortfall in weeks rather than in tomestones: "three weeks early" is a thing
            // somebody can fix by moving the row, and "410 short" is a number they then have to
            // divide by 450 themselves.
            var short_ = owed.Cost - player.Tomes;
            var weeks = (short_ + tier.TomestonesPerWeek - 1) / Math.Max(1, tier.TomestonesPerWeek);

            problems.Add(new PlanProblem(currentWeek, what,
                                         $"{player.Name} cannot afford it in week {currentWeek} — " +
                                         $"{short_} tomestones short, about {weeks} week(s) too early."));
            return;
        }

        player.TakeTome(owed.Slot);

        awards.Add(new PlannedAward(currentWeek, 0, Slots.CofferSlot(owed.Slot), null,
                                    player.Key, player.Name, Bought: true, TomeCost: owed.Cost,
                                    Manual: true));
    }

    private void BuyPinnedWithBooks(PlayerPlan player, ManualAward pin, string what)
    {
        // Same reason as above: an index says "not found" without a struct having to look empty.
        var at = player.Open.FindIndex(n => pin.Upgrade != null
                                                ? n.IsUpgrade && n.Side == pin.Upgrade.Value
                                                : !n.IsUpgrade && pin.Slot != null &&
                                                  Slots.CofferSlot(n.Slot) == Slots.CofferSlot(pin.Slot.Value));

        if (at < 0)
        {
            problems.Add(new PlanProblem(currentWeek, what,
                                         $"{player.Name} does not owe that — already had, or never planned."));
            return;
        }

        var need = player.Open[at];

        var cost = BookLedger.CostOf(tier, need);

        if (cost == null)
        {
            problems.Add(new PlanProblem(currentWeek, what, "This tier has no book price for that."));
            return;
        }

        if (!BookLedger.CanAfford(tier, player, cost))
        {
            var fight = tier.Encounter(cost.Encounter)?.Name ?? $"#{cost.Encounter}";

            problems.Add(new PlanProblem(currentWeek, what,
                                         $"{player.Name} has not got {cost.Cost} × {fight} by week " +
                                         $"{currentWeek}."));
            return;
        }

        if (!BookLedger.Pay(tier, player, cost, out var traded))
            return;

        player.Open.Remove(need);

        awards.Add(new PlannedAward(currentWeek, cost.Encounter, Slots.CofferSlot(need.Slot),
                                    need.IsUpgrade ? need.Side : null,
                                    player.Key, player.Name, Bought: true, Traded: traded,
                                    Manual: true));
    }

    /// <summary>
    /// Whether a purchase is spoken for in some other week.
    ///
    /// The greedy passes below buy whatever they can afford, and left alone they would buy a thing
    /// somebody pinned for week four in week one — which does not break the pin so much as make it
    /// pointless, silently and a week early. A pin is a decision about *when* as much as about who.
    /// </summary>
    private bool ReservedElsewhere(string playerKey, GearSlot? slot, GearSide? upgrade)
    {
        foreach (var pin in manual.Awards)
        {
            if (!pin.Bought || pin.Week == currentWeek || pin.PlayerKey != playerKey)
                continue;

            var sameSlot = slot != null && pin.Slot != null &&
                           Slots.CofferSlot(pin.Slot.Value) == Slots.CofferSlot(slot.Value);

            if (upgrade != null ? pin.Upgrade == upgrade : sameSlot && pin.Upgrade == null)
                return true;
        }

        return false;
    }

    private void SpendTokens(IReadOnlyList<PlayerPlan> players)
    {
        foreach (var player in players)
        {
            while (true)
            {
                var affordable = player.Open
                                       .Where(need => !ReservedElsewhere(player.Key, need.Slot,
                                                                         need.IsUpgrade ? need.Side : null))
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

            if (player.IsTomeDone && player.TomeFinishedWeek < 0)
                player.TomeFinishedWeek = week;
        }
    }
}
