using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Planning.Dps;
using LootMastr.Roster;

var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {name}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
    if (!ok) failures++;
}

// A tier shaped like AAC Heavyweight: accessories in M1S, head/hands/feet + accessory upgrade in
// M2S, body/legs + armour and weapon upgrades in M3S, weapon in M4S.
static TierDefinition Tier()
{
    var tier = new TierDefinition
    {
        Id = "test", Name = "Test tier",
        RaidItemLevel = 790, RaidWeaponItemLevel = 795, TomeItemLevel = 780, AugmentedItemLevel = 790,
        Upgrades =
        {
            new TierUpgrade { Side = GearSide.Right, ItemName = "Glaze", ItemId = 1 },
            new TierUpgrade { Side = GearSide.Left, ItemName = "Twine", ItemId = 2 },
            new TierUpgrade { Side = GearSide.Weapon, ItemName = "Solvent", ItemId = 3 },
        },
        Encounters =
        {
            new TierEncounter
            {
                Index = 1, Name = "M1S", TokenItemName = "Book I", TokenItemId = 11, DropCount = 2,
                DropSlots = { GearSlot.Earrings, GearSlot.Necklace, GearSlot.Bracelets, GearSlot.Ring1 },
            },
            new TierEncounter
            {
                Index = 2, Name = "M2S", TokenItemName = "Book II", TokenItemId = 12, DropCount = 2,
                DropSlots = { GearSlot.Head, GearSlot.Hands, GearSlot.Feet },
                UpgradeDrops = { GearSide.Right },
            },
            new TierEncounter
            {
                Index = 3, Name = "M3S", TokenItemName = "Book III", TokenItemId = 13, DropCount = 2,
                DropSlots = { GearSlot.Body, GearSlot.Legs },
                UpgradeDrops = { GearSide.Left, GearSide.Weapon },
            },
            new TierEncounter
            {
                Index = 4, Name = "M4S", TokenItemName = "Book IV", TokenItemId = 14, DropCount = 2,
                DropSlots = { GearSlot.Weapon },
            },
        },
    };

    // The real cost table: uniform per category.
    void Rule(string label, int encounter, int cost, GearSlot[] slots, GearSide? upgrade = null) =>
        tier.CostRules.Add(new TierCostRule
        {
            Label = label, Encounter = encounter, Cost = cost,
            Slots = [..slots], Upgrade = upgrade,
        });

    Rule("Accessories", 1, 3, [GearSlot.Earrings, GearSlot.Necklace, GearSlot.Bracelets, GearSlot.Ring1]);
    Rule("Head, hands, feet", 2, 4, [GearSlot.Head, GearSlot.Hands, GearSlot.Feet]);
    Rule("Accessory upgrade", 2, 3, [], GearSide.Right);
    Rule("Body, legs", 3, 6, [GearSlot.Body, GearSlot.Legs]);
    Rule("Armour upgrade", 3, 4, [], GearSide.Left);
    Rule("Weapon upgrade", 3, 4, [], GearSide.Weapon);
    Rule("Weapon (with shield)", 4, 8, [GearSlot.Weapon]);
    

    // The last fight's books trade one for one into any earlier fight's.
    tier.Conversions.Add(new TierTokenConversion { FromEncounter = 4, ToEncounters = [1, 2, 3], Ratio = 1 });

    tier.SeedTomeCosts();

    return tier;
}

static RosterMember Member(string name, params (GearSlot Slot, GearSource Source)[] needs)
{
    var member = new RosterMember { Name = name, World = "Test" };
    foreach (var (slot, src) in needs)
        member.NeedFor(slot).Source = src;

    return member;
}

static PlayerPlan Plan(RosterMember member, RaidRole role, TierDefinition tier, int order = 0) =>
    PlayerPlan.From(member, role, tier, order);

var tier = Tier();
var rules = new PriorityRules();

// --- need list construction --------------------------------------------------------------------

{
    var m = Member("A",
                   (GearSlot.Body, GearSource.Raid),
                   (GearSlot.Legs, GearSource.TomeAugmented),
                   (GearSlot.Head, GearSource.Tome),
                   (GearSlot.Feet, GearSource.Crafted),
                   (GearSlot.Weapon, GearSource.None));

    var plan = Plan(m, RaidRole.Dps, tier);

    Check("only raid and augmented slots become needs", plan.Open.Count == 2,
          string.Join(", ", plan.Open.Select(n => n.Describe())));

    Check("raid body comes from M3S", plan.Open.Any(n => n is { IsUpgrade: false, Slot: GearSlot.Body, Encounter: 3 }));
    Check("augmented legs need the M3S armour material",
          plan.Open.Any(n => n is { IsUpgrade: true, Side: GearSide.Left, Encounter: 3 }));

    m.NeedFor(GearSlot.Body).Obtained = true;
    m.NeedFor(GearSlot.Legs).UpgradeObtained = true;
    Check("ticked off slots drop out", Plan(m, RaidRole.Dps, tier).Open.Count == 0);
}

// --- augmented tome gear is recognised by name --------------------------------------------------

{
    // Augmented pieces sit at the raid item level, so the name is the only signal before the
    // upgrade trade has been discovered. Getting this wrong files every one of them as a raid drop.
    Check("\"Augmented\" is tome gear", tier.IsAugmentedName("Augmented Bygone Brass Coat"));
    Check("\"Aug.\" is tome gear", tier.IsAugmentedName("Aug. Bygone Brass Coat"));
    Check("matching ignores case", tier.IsAugmentedName("augmented bygone brass coat"));

    Check("a raid piece is not tome gear", !tier.IsAugmentedName("Grand Champion's Coat"));
    Check("the plain tome piece is not augmented", !tier.IsAugmentedName("Bygone Brass Coat"));

    // The prefix has to be a prefix, or "Augmentation Ring" and friends would be swept up too.
    Check("the word has to start the name", !tier.IsAugmentedName("Ring of Augmented Power"));
    Check("an empty name is not augmented", !tier.IsAugmentedName(""));
}

// --- gear is classified from item level and name alone --------------------------------------------

{
    // The tier is raid 790 / raid weapon 795 / tome 780 -> augmented 790. Raid gear and augmented
    // tome gear share an item level, so the name is the whole of the distinction. None of this may
    // depend on the shop discovery having run.
    Check("raid armour is raid",
          tier.ClassifyByLevel(790, "Grand Champion's Coat") == GearSource.Raid);

    Check("raid weapons are raid",
          tier.ClassifyByLevel(795, "Grand Champion's Blade") == GearSource.Raid);

    Check("augmented tome gear at the same level is not raid",
          tier.ClassifyByLevel(790, "Augmented Bygone Brass Coat") == GearSource.TomeAugmented);

    Check("an augmented weapon at the raid weapon level is still tome gear",
          tier.ClassifyByLevel(795, "Augmented Bygone Brass Blade") == GearSource.TomeAugmented);

    Check("plain tome gear is tome",
          tier.ClassifyByLevel(780, "Bygone Brass Coat") == GearSource.Tome);

    Check("the abbreviated spelling counts too",
          tier.ClassifyByLevel(790, "Aug. Bygone Brass Coat") == GearSource.TomeAugmented);

    // Anything the levels do not account for is handed back for the shop data to answer.
    Check("an unrelated item level is left undecided", tier.ClassifyByLevel(710, "Crafted Coat") == null);
    Check("no item level is left undecided", tier.ClassifyByLevel(0, "Something") == null);
}

// --- the slot is read out of a coffer's name ------------------------------------------------------

{
    var accessories = new[] { GearSlot.Earrings, GearSlot.Necklace, GearSlot.Bracelets, GearSlot.Ring1 };
    var armour = new[] { GearSlot.Head, GearSlot.Hands, GearSlot.Feet };
    var weapons = new[] { GearSlot.Weapon };

    Check("head coffer reads as head",
          Slots.SlotFromName("Grand Champion's Head Gear Coffer (IL 790)", armour) == GearSlot.Head);

    Check("foot coffer reads as feet",
          Slots.SlotFromName("Grand Champion's Foot Gear Coffer (IL 790)", armour) == GearSlot.Feet);

    // "Earring" contains "ring": the ordering inside SlotWords is what stops this landing on Ring1.
    Check("earring coffer does not read as a ring",
          Slots.SlotFromName("Grand Champion's Earring Coffer (IL 790)", accessories) == GearSlot.Earrings);

    Check("ring coffer reads as a ring",
          Slots.SlotFromName("Grand Champion's Ring Coffer (IL 790)", accessories) == GearSlot.Ring1);

    // A shield is part of the weapon purchase, so even a coffer named for it files under the weapon.
    Check("shield coffer reads as the weapon",
          Slots.SlotFromName("Grand Champion's Shield Coffer (IL 790)", weapons) == GearSlot.Weapon);

    // Discovery now matches against every slot, since restricting it to what the tier claims a
    // fight drops mostly just left rows unassigned when those pools were slightly off.
    Check("matching against every slot still reads the head coffer",
          Slots.SlotFromName("Grand Champion's Head Gear Coffer (IL 790)", Slots.All) == GearSlot.Head);

    Check("and still does not turn an earring into a ring",
          Slots.SlotFromName("Grand Champion's Earring Coffer (IL 790)", Slots.All) == GearSlot.Earrings);

    // A narrowed candidate list is still honoured where one is passed.
    Check("a slot outside the candidates is never returned",
          Slots.SlotFromName("Grand Champion's Head Gear Coffer (IL 790)", accessories) == null);

    Check("an unreadable name gives nothing",
          Slots.SlotFromName("Thundersteeped Twine", armour) == null);

    Check("no candidates gives nothing", Slots.SlotFromName("Head Gear Coffer", []) == null);
}

// --- target vs actual --------------------------------------------------------------------------

{
    // Nothing handed over yet.
    var owed = new SlotNeed { Source = GearSource.Raid, BisItemId = 500 };
    Check("an unmet raid slot is owed", owed.StateFor(scanned: true) == SlotState.Needed);
    Check("scanning does not change an owed slot", owed.StateFor(scanned: false) == SlotState.Needed);

    // Handed over and worn.
    var worn = new SlotNeed { Source = GearSource.Raid, BisItemId = 500, Obtained = true, EquippedItemId = 500 };
    Check("handed over and worn is done", worn.StateFor(scanned: true) == SlotState.Done);

    // Handed over, wearing something else. This is the case that has to stand out.
    var unopened = new SlotNeed { Source = GearSource.Raid, BisItemId = 500, Obtained = true, EquippedItemId = 400 };
    Check("handed over but wearing something else is flagged",
          unopened.StateFor(scanned: true) == SlotState.AssignedNotWorn);

    // Handed over, wearing nothing at all in that slot.
    var empty = new SlotNeed { Source = GearSource.Raid, BisItemId = 500, Obtained = true };
    Check("handed over with an empty slot is flagged", empty.StateFor(scanned: true) == SlotState.AssignedNotWorn);

    // The point of the scanned flag: before anyone has looked, "not worn" is not knowable, and
    // guessing would put a warning on every row of a fresh roster.
    Check("nothing is flagged before a scan", empty.StateFor(scanned: false) == SlotState.Done);

    // A slot set by hand has no BiS id, so the kind of the equipped item has to carry it.
    var byHand = new SlotNeed
    {
        Source = GearSource.Raid, Obtained = true,
        EquippedItemId = 400, EquippedSource = GearSource.Raid,
    };
    Check("a hand-set slot matches on the kind of item", byHand.StateFor(scanned: true) == SlotState.Done);

    var wrongKind = new SlotNeed
    {
        Source = GearSource.Raid, Obtained = true,
        EquippedItemId = 400, EquippedSource = GearSource.Crafted,
    };
    Check("wearing crafted where raid was planned is flagged",
          wrongKind.StateFor(scanned: true) == SlotState.AssignedNotWorn);

    // Slots that cost the raid nothing never carry a state at all.
    var crafted = new SlotNeed { Source = GearSource.Crafted };
    Check("a crafted slot is never planned", crafted.StateFor(scanned: true) == SlotState.NotPlanned);
}

// --- the same coffer twice in one chest -----------------------------------------------------------

{
    // Old chests really do drop two of a kind — the recorded Deltascape chest had two earring
    // coffers and two of another. Since the gear is unique, the second cannot go to whoever is
    // taking the first, so a decision has to be made with the ones above it already counted.
    var a = Member("A", (GearSlot.Earrings, GearSource.Raid));
    var b = Member("B", (GearSlot.Earrings, GearSource.Raid));

    var plans = new List<PlayerPlan> { Plan(a, RaidRole.Dps, tier), Plan(b, RaidRole.Dps, tier) };

    Check("both want the first earring coffer", plans.Count(p => p.Wants(GearSlot.Earrings)) == 2);

    // Hand the first one over, the way the loot tab does before ranking the next item.
    plans[0].TakeSlot(GearSlot.Earrings);

    Check("only one is left wanting the second", plans.Count(p => p.Wants(GearSlot.Earrings)) == 1);
    Check("and it is the other player", plans.Single(p => p.Wants(GearSlot.Earrings)).Name == "B");
}

// --- an unworn but awarded piece is still out of the distribution --------------------------------

{
    // The rule the whole thing turns on: a coffer that was handed over but never opened must not
    // come back around, or the same player is assigned it twice.
    var m = Member("A", (GearSlot.Body, GearSource.Raid));
    var need = m.NeedFor(GearSlot.Body);
    need.Obtained = true;
    need.EquippedItemId = 0;

    Check("an awarded but unworn slot is satisfied", need.IsSatisfied);

    var plan = Plan(m, RaidRole.Dps, tier);
    Check("an awarded but unworn slot is not a need", plan.Open.Count == 0);
    Check("and the player is not a candidate for it", !plan.Wants(GearSlot.Body));
}

// --- nobody needs it ---------------------------------------------------------------------------

{
    var plans = new List<PlayerPlan> { Plan(Member("A", (GearSlot.Body, GearSource.Raid)), RaidRole.Dps, tier) };
    Check("a player who wants nothing else is not a candidate", !plans[0].Wants(GearSlot.Head));
    Check("a player who wants it is a candidate", plans[0].Wants(GearSlot.Body));
}

// --- rings share a coffer ----------------------------------------------------------------------

{
    var m = Member("A", (GearSlot.Ring2, GearSource.Raid));
    var plan = Plan(m, RaidRole.Dps, tier);
    Check("a ring 2 need is filled by the ring coffer", plan.Wants(GearSlot.Ring1) && plan.Wants(GearSlot.Ring2));
    Check("taking the ring coffer clears the ring 2 need", plan.TakeSlot(GearSlot.Ring1) && plan.Open.Count == 0);
}

// --- everyone wants the same piece ---------------------------------------------------------------

{
    var members = Enumerable.Range(1, 8)
                            .Select(i => Member($"P{i}", (GearSlot.Body, GearSource.Raid)))
                            .ToList();

    var plans = members.Select(m => Plan(m, RaidRole.Dps, tier)).ToList();
    var result = new WeekSimulator(tier, rules, 12).Run(plans);

    // M3S puts up two body-or-legs coffers a week and six books buy one, so eight players wanting
    // the same piece must be done well inside the horizon rather than never.
    Check("eight players on one slot all finish", !result.BeyondHorizon(result.LastFinishWeek),
          $"last week {result.LastFinishWeek}");

    Check("nobody finishes in week 0", result.FinishWeeks.Values.All(w => w >= 1));
}

// --- books alone are enough ----------------------------------------------------------------------

{
    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    var plan = Plan(m, RaidRole.Dps, tier);

    // Alone, the weapon drops every week, so it lands in week 1.
    var solo = new WeekSimulator(tier, rules, 12).Run([plan]);
    Check("an uncontested drop lands immediately", solo.LastFinishWeek == 1, $"week {solo.LastFinishWeek}");
}

{
    // Same need, but the coffer is taken away: eight books at one a week means week 8. The pool is
    // emptied rather than the drop count zeroed, because every coffer drops by default now and a
    // count of zero says nothing in that mode.
    var noDrops = Tier();
    noDrops.Encounter(4)!.DropSlots.Clear();

    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    var result = new WeekSimulator(noDrops, rules, 12).Run([Plan(m, RaidRole.Dps, noDrops)]);
    Check("with no drops the weapon is bought on book eight", result.LastFinishWeek == 8,
          $"week {result.LastFinishWeek}");
}

{
    // Starting with books already in hand pulls that in.
    var noDrops = Tier();
    noDrops.Encounter(4)!.DropSlots.Clear();

    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    m.Tokens[4] = 6;

    var result = new WeekSimulator(noDrops, rules, 12).Run([Plan(m, RaidRole.Dps, noDrops)]);
    Check("books already held count", result.LastFinishWeek == 2, $"week {result.LastFinishWeek}");
}

{
    // Week 1 is one book from every fight, week 2 is two, on top of whatever is already held. The
    // projection used to be run as "the coming week, then the rest from week 2", and since only the
    // simulator hands books out, week 1 quietly gave nobody anything.
    var noDrops = Tier();
    noDrops.Encounter(4)!.DropSlots.Clear();

    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    m.Tokens[4] = 7;

    var result = new WeekSimulator(noDrops, rules, 12).Run([Plan(m, RaidRole.Dps, noDrops)]);
    Check("the first week's book is earned in the first week", result.LastFinishWeek == 1,
          $"week {result.LastFinishWeek}");
}

{
    // Downtrading, inside the schedule rather than in the ledger on its own: nothing this player
    // needs takes the last fight's books, so all eight of them are spare and trade down.
    var noDrops = Tier();
    foreach (var encounter in noDrops.Encounters)
        encounter.DropSlots.Clear();

    var m = Member("A", (GearSlot.Necklace, GearSource.Raid));
    m.Tokens[4] = 3;

    var result = new WeekSimulator(noDrops, rules, 12).Run([Plan(m, RaidRole.Dps, noDrops)]);
    Check("spare last-fight books are traded down to finish sooner", result.LastFinishWeek == 1,
          $"week {result.LastFinishWeek}");

    // The trade is reported, not just performed: three accessory books, one of them earned this
    // week and two traded for. A plan saying "buy it with three M1S books" means something else if
    // two of the three have to be exchanged first.
    var buy = result.Awards.Single(a => a.Bought);
    Check("and the plan says which books were traded in",
          buy.Traded is { FromEncounter: 4, Books: 2, Covered: 2 },
          buy.Traded?.ToString() ?? "nothing recorded");

    var rich = Member("C", (GearSlot.Necklace, GearSource.Raid));
    rich.Tokens[1] = 3;

    Check("a purchase paid for out of its own books records no trade",
          new WeekSimulator(noDrops, rules, 12)
              .Run([Plan(rich, RaidRole.Dps, noDrops)])
              .Awards.Single(a => a.Bought).Traded == null);

    // In a tier that does not trade books down at all, the same player waits three weeks for three
    // accessory books. Note that dropping the three in hand is not the comparison to make: every
    // week hands out a fourth-fight book too, and with no weapon owed those are spare as well.
    var noTrades = Tier();
    foreach (var encounter in noTrades.Encounters)
        encounter.DropSlots.Clear();

    noTrades.Conversions.Clear();

    var poorer = Member("B", (GearSlot.Necklace, GearSource.Raid));
    poorer.Tokens[4] = 3;

    var slow = new WeekSimulator(noTrades, rules, 12).Run([Plan(poorer, RaidRole.Dps, noTrades)]);
    Check("and a tier without downtrading makes them wait for their own", slow.LastFinishWeek == 3,
          $"week {slow.LastFinishWeek}");
}

{
    // Books in hand were earned by clears that have already happened, so they can be spent before
    // this week's fights — and something bought is something that no longer needs to be won.
    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    m.Tokens[4] = 8;

    var plan = Plan(m, RaidRole.Dps, tier);
    var bought = new WeekSimulator(tier, rules, 12).SpendNow([plan], 1);

    Check("books already in hand are spent before the coming week", bought.Count == 1,
          string.Join(", ", bought.Select(a => a.What)));

    Check("and what they bought is marked as bought, in week 1",
          bought.Count == 1 && bought[0] is { Bought: true, Week: 1, Slot: GearSlot.Weapon });

    Check("leaving nothing left to win", plan.IsDone);
}

// --- one line per thing, so a plan never looks like it counted twice ------------------------------

{
    // Augmented head, body and legs is three separate armour materials. They used to be labelled by
    // side, so all three read "Left upgrade" — and a week handing out two of them showed the same
    // line twice, once plain and once "(books)", which reads as double-counting rather than as two
    // materials. Naming them after the piece is what tells them apart.
    var m = Member("A",
                   (GearSlot.Head, GearSource.TomeAugmented),
                   (GearSlot.Body, GearSource.TomeAugmented),
                   (GearSlot.Legs, GearSource.TomeAugmented));

    var plan = Plan(m, RaidRole.Dps, tier);

    Check("three augmented left-side pieces are three needs", plan.Open.Count == 3,
          string.Join(", ", plan.Open.Select(n => n.Describe())));

    Check("and they are told apart by the piece, not the side",
          plan.Open.Select(n => n.Describe()).Distinct().Count() == 3,
          string.Join(", ", plan.Open.Select(n => n.Describe())));

    var result = new WeekSimulator(tier, rules, 12).Run([Plan(m, RaidRole.Dps, tier)]);

    var repeated = result.Awards
                         .GroupBy(a => (a.Week, a.PlayerKey, a.What))
                         .Where(g => g.Count() > 1)
                         .ToList();

    Check("so no player is given the same thing twice in a week", repeated.Count == 0,
          string.Join(", ", repeated.Select(g => $"{g.Key.What} ×{g.Count()}")));

    Check("and every material names the piece it is for",
          result.Awards.Where(a => a.Upgrade != null).All(a => a.Slot != null),
          string.Join(", ", result.Awards.Where(a => a.Upgrade != null).Select(a => a.What)));
}

// --- every coffer drops, by default --------------------------------------------------------------

{
    Check("a fresh tier has every coffer dropping", new TierDefinition().AllCoffersDrop);

    // Four accessories in the pool and a drop count of two: the count is what the projection uses
    // for a tier that says otherwise, and is ignored while this is on.
    var pool = WeekSimulator.DropsFor(tier, tier.Encounter(1)!, 1).ToList();
    Check("so a fight puts up its whole pool", pool.Count == 4, string.Join(", ", pool));

    var rationed = Tier();
    rationed.AllCoffersDrop = false;
    Check("turned off, the drop count is what drops",
          WeekSimulator.DropsFor(rationed, rationed.Encounter(1)!, 1).Count() == 2);
}

// --- book costs come from the rules ---------------------------------------------------------------

{
    Check("every accessory costs three T1 books",
          tier.CostForSlot(GearSlot.Necklace) == new BookCost(1, 3) &&
          tier.CostForSlot(GearSlot.Ring2) == new BookCost(1, 3));

    Check("head, hands and feet cost four T2 books", tier.CostForSlot(GearSlot.Feet) == new BookCost(2, 4));
    Check("body and legs cost six T3 books", tier.CostForSlot(GearSlot.Body) == new BookCost(3, 6));
    Check("the weapon costs eight T4 books", tier.CostForSlot(GearSlot.Weapon) == new BookCost(4, 8));
    Check("the accessory upgrade costs three T2 books", tier.CostForUpgrade(GearSide.Right) == new BookCost(2, 3));
    Check("the armour upgrade costs four T3 books", tier.CostForUpgrade(GearSide.Left) == new BookCost(3, 4));
    Check("the weapon upgrade costs four T3 books", tier.CostForUpgrade(GearSide.Weapon) == new BookCost(3, 4));
}

// --- shop prices are the common one, not the cheapest ---------------------------------------------

{
    // A tier with no rules of its own falls back on the shop. Old tiers sell the shield separately
    // and cheaply, and it sits under the weapon slot — taking the minimum priced every weapon in
    // Deltascape at three books.
    var shop = Tier();
    shop.CostRules.Clear();

    void Sell(int cost, string name, GearSlot slot) =>
        shop.Rewards.Add(new TierReward
        {
            Encounter = 4, Cost = cost, ItemId = (uint)(900 + shop.Rewards.Count),
            ItemName = name, Slot = slot,
        });

    Sell(3, "Genji Shield", GearSlot.Weapon);
    Sell(8, "Genji Blade", GearSlot.Weapon);
    Sell(8, "Genji Cane", GearSlot.Weapon);
    Sell(8, "Genji Rod", GearSlot.Weapon);

    Check("the weapon costs what most weapons cost", shop.CostForSlot(GearSlot.Weapon) == new BookCost(4, 8),
          $"{shop.CostForSlot(GearSlot.Weapon)}");

    Check("and the odd cheap one does not set the price",
          shop.CostForSlot(GearSlot.Weapon)!.Cost != 3);

    // A typed-in rule still wins over the shop.
    shop.CostRules.Add(new TierCostRule { Label = "Weapon", Encounter = 4, Cost = 6, Slots = [GearSlot.Weapon] });
    Check("a rule beats the shop", shop.CostForSlot(GearSlot.Weapon) == new BookCost(4, 6));
}

// --- every coffer dropping, or a set number ------------------------------------------------------

{
    // Four accessories every week, so one player alone is done in week one.
    var everything = Tier();
    everything.AllCoffersDrop = true;

    var accessories = new[]
    {
        (GearSlot.Earrings, GearSource.Raid), (GearSlot.Necklace, GearSource.Raid),
        (GearSlot.Bracelets, GearSource.Raid), (GearSlot.Ring1, GearSource.Raid),
    };

    var m = Member("A", accessories);
    var all = new WeekSimulator(everything, rules, 12).Run([Plan(m, RaidRole.Dps, everything)]);
    Check("with every coffer dropping, all four accessories land in week one",
          all.LastFinishWeek == 1, $"week {all.LastFinishWeek}");

    // Two out of the pool of four, so it takes two weeks.
    var some = Tier();
    some.AllCoffersDrop = false;
    some.Encounter(1)!.DropCount = 2;

    var m2 = Member("B", accessories);
    var partial = new WeekSimulator(some, rules, 12).Run([Plan(m2, RaidRole.Dps, some)]);
    Check("with two of four, the same player needs two weeks",
          partial.LastFinishWeek == 2, $"week {partial.LastFinishWeek}");

    // The count is ignored entirely when everything drops.
    everything.Encounter(1)!.DropCount = 0;
    var ignored = new WeekSimulator(everything, rules, 12).Run([Plan(Member("C", accessories), RaidRole.Dps, everything)]);
    Check("the drop count is ignored when every coffer drops", ignored.LastFinishWeek == 1,
          $"week {ignored.LastFinishWeek}");
}

// --- trading the last fight's books in ------------------------------------------------------------

{
    // Someone who only needs a necklace, holding nothing but weapon books.
    var m = Member("A", (GearSlot.Necklace, GearSource.Raid));
    m.Tokens[4] = 3;

    var plan = Plan(m, RaidRole.Dps, tier);

    Check("spare last-fight books count towards an earlier cost",
          BookLedger.Available(tier, plan, 1) == 3, $"{BookLedger.Available(tier, plan, 1)}");

    Check("and they can pay for it", BookLedger.CanAfford(tier, plan, tier.CostForSlot(GearSlot.Necklace)!));

    BookLedger.Pay(tier, plan, tier.CostForSlot(GearSlot.Necklace)!);
    Check("paying takes them out of the fourth fight's pile", plan.Tokens[4] == 0 && plan.Tokens[1] == 0);
}

{
    // The trap: the same books are what buys the weapon. Someone who still needs it must not have
    // them traded away, or the forecast reports a finish that never happens.
    var m = Member("A", (GearSlot.Necklace, GearSource.Raid), (GearSlot.Weapon, GearSource.Raid));
    m.Tokens[4] = 3;

    var plan = Plan(m, RaidRole.Dps, tier);

    Check("books reserved for the weapon are not spare",
          BookLedger.Spare(tier, plan, 4) == 0, $"{BookLedger.Spare(tier, plan, 4)}");

    Check("so the necklace is not affordable yet",
          !BookLedger.CanAfford(tier, plan, tier.CostForSlot(GearSlot.Necklace)!));
}

{
    // Once the weapon is no longer owed, the surplus above it frees up.
    var m = Member("A", (GearSlot.Necklace, GearSource.Raid), (GearSlot.Weapon, GearSource.Raid));
    m.NeedFor(GearSlot.Weapon).Obtained = true;
    m.Tokens[4] = 3;

    var plan = Plan(m, RaidRole.Dps, tier);
    Check("with the weapon done the books are spare again", BookLedger.Spare(tier, plan, 4) == 3);
}

{
    // Own books first: only the shortfall is converted.
    var m = Member("A", (GearSlot.Necklace, GearSource.Raid));
    m.Tokens[1] = 2;
    m.Tokens[4] = 5;

    var plan = Plan(m, RaidRole.Dps, tier);
    BookLedger.Pay(tier, plan, tier.CostForSlot(GearSlot.Necklace)!);

    Check("own books are spent before anything is traded in",
          plan.Tokens[1] == 0 && plan.Tokens[4] == 4, $"T1 {plan.Tokens[1]}, T4 {plan.Tokens[4]}");
}

{
    // Conversion only runs one way.
    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    m.Tokens[1] = 20;

    var plan = Plan(m, RaidRole.Dps, tier);
    Check("earlier books never buy the last fight's rewards",
          !BookLedger.CanAfford(tier, plan, tier.CostForSlot(GearSlot.Weapon)!));
}

// --- the one rule: role gate, player order, how much to share out --------------------------------

// The whole loot policy is these three settings, and every table in the plugin goes through the
// same DropOrder.Rank. What is asserted here is that each of them does what its label says, and
// that the slider actually reaches both ends rather than hovering near the middle.

{
    static string Won(PriorityRules with, params Contender[] candidates) =>
        DropOrder.Rank(with, candidates)[0].Who.Key;

    // Same role, neither has won anything, different amounts left: only the slider separates them.
    var top = new Contender("Top", RaidRole.Dps, 0, 0, 1);
    var behind = new Contender("Behind", RaidRole.Dps, 1, 0, 4);

    Check("shared out, the drop goes to the player with more left",
          Won(new PriorityRules { Spread = 1.0 }, top, behind) == "Behind");

    Check("funnelled, it goes to the top of the player order instead",
          Won(new PriorityRules { Spread = 0.0 }, top, behind) == "Top");

    Check("with the player order off, funnelling has nothing to funnel towards",
          Won(new PriorityRules { Spread = 0.0, UsePlayerOrder = false }, top, behind) == "Behind");

    // Two candidates, so the positions are 0 and 1 either way round and the halves cancel exactly.
    // The tie falls to the declared order, which is the half that was asked for out loud.
    Check("evenly weighed, a tie falls to the player order",
          Won(new PriorityRules { Spread = 0.5 }, top, behind) == "Top");

    // Sharing out counts what people have already been given, not only what they still owe.
    var served = new Contender("Served", RaidRole.Dps, 0, 3, 2);
    var missed = new Contender("Missed", RaidRole.Dps, 1, 0, 2);

    Check("shared out, whoever has won least goes first",
          Won(new PriorityRules { Spread = 1.0 }, served, missed) == "Missed");

    Check("funnelled, what they have already won does not count",
          Won(new PriorityRules { Spread = 0.0 }, served, missed) == "Served");

    // And the role gate sits above all of it.
    var healer = new Contender("Healer", RaidRole.Healer, 0, 0, 5);
    var dps = new Contender("Dps", RaidRole.Dps, 1, 3, 1);

    Check("a healer ahead on every other count still waits behind a damage dealer",
          Won(new PriorityRules { Spread = 1.0 }, healer, dps) == "Dps");

    Check("with the role order off, they do not",
          Won(new PriorityRules { Spread = 1.0, UseRoleOrder = false }, healer, dps) == "Healer");
}

// --- the slider moves with the distance, not just past the middle --------------------------------

{
    // It used to mix two rank positions, so with two candidates the scores were s against 1 - s and
    // the order flipped at exactly 0.5 however far apart the two were. One item already won now
    // counts as one place in the order, so the tipping point sits where the difference is.
    static string Won(double spread, params Contender[] candidates) =>
        DropOrder.Rank(new PriorityRules { Spread = spread }, candidates)[0].Who.Key;

    // First in the order, three items in hand, against someone with nothing: passed at a quarter.
    var hoarding = new Contender("Hoarding", RaidRole.Dps, 0, 3, 2);
    var empty = new Contender("Empty", RaidRole.Dps, 1, 0, 2);

    Check("three items ahead is passed well before the middle",
          Won(0.2, hoarding, empty) == "Hoarding" && Won(0.3, hoarding, empty) == "Empty",
          $"0.2 -> {Won(0.2, hoarding, empty)}, 0.3 -> {Won(0.3, hoarding, empty)}");

    // One item ahead: the old behaviour, and the calibration this is anchored on.
    var slightly = new Contender("Slightly", RaidRole.Dps, 0, 1, 2);

    Check("one item ahead still turns over at the middle",
          Won(0.45, slightly, empty) == "Slightly" && Won(0.55, slightly, empty) == "Empty",
          $"0.45 -> {Won(0.45, slightly, empty)}, 0.55 -> {Won(0.55, slightly, empty)}");

    // Level on items won: the top of the order keeps it wherever the slider sits, because there is
    // no reason to pass over somebody who has had no more than anyone else.
    var level = new Contender("Level", RaidRole.Dps, 0, 0, 2);

    Check("nobody is passed over for someone no needier", Won(1.0, level, empty) == "Level");

    // And with three, the same: the gap decides, not the midpoint.
    var third = new Contender("Third", RaidRole.Dps, 2, 0, 2);

    Check("with three candidates the same distance rule holds",
          Won(0.3, hoarding, empty, third) == "Empty" && Won(0.1, hoarding, empty, third) == "Hoarding",
          $"0.1 -> {Won(0.1, hoarding, empty, third)}, 0.3 -> {Won(0.3, hoarding, empty, third)}");
}

{
    // Funnelling means one player's whole list, not one lucky coffer.
    var first = Member("First", (GearSlot.Body, GearSource.Raid), (GearSlot.Legs, GearSource.Raid));
    var second = Member("Second", (GearSlot.Body, GearSource.Raid), (GearSlot.Legs, GearSource.Raid));

    var focused = new PriorityRules { Spread = 0.0 };

    var result = new WeekSimulator(tier, focused, 12)
        .Run([Plan(first, RaidRole.Dps, tier, 0), Plan(second, RaidRole.Dps, tier, 1)]);

    var week1 = result.Awards.Where(a => a.Week == 1 && !a.Bought).Select(a => a.PlayerName).Distinct().ToList();

    Check("funnelled, the first week's coffers all go to the same player",
          week1.Count == 1 && week1[0] == "First", string.Join(", ", week1));

    Check("and the other player is still finished inside the horizon",
          !result.BeyondHorizon(result.FinishWeeks[Plan(second, RaidRole.Dps, tier, 1).Key]),
          $"W{result.FinishWeeks[Plan(second, RaidRole.Dps, tier, 1).Key]}");
}

// --- roles are a queue, not a weight -------------------------------------------------------------

{
    Check("damage is geared first by default", rules.RankOf(RaidRole.Dps) == 0);
    Check("then tanks", rules.RankOf(RaidRole.Tank) == 1);
    Check("then healers", rules.RankOf(RaidRole.Healer) == 2);
    Check("and the role order is on by default", rules.UseRoleOrder);
    Check("as is the player order", rules.UsePlayerOrder);
    Check("with the loot half shared out", Math.Abs(rules.Spread - 0.5) < 0.001);

    var dps = Member("Dps", (GearSlot.Body, GearSource.Raid));
    var tank = Member("Tank", (GearSlot.Body, GearSource.Raid));
    var healer = Member("Healer", (GearSlot.Body, GearSource.Raid));

    List<PlayerPlan> Three() =>
    [
        Plan(healer, RaidRole.Healer, tier), Plan(tank, RaidRole.Tank, tier), Plan(dps, RaidRole.Dps, tier),
    ];

    var result = new WeekSimulator(tier, rules, 12).Run(Three());
    var order = result.Awards.Where(a => a.Slot == GearSlot.Body).Select(a => a.PlayerName).ToList();

    Check("the first body coffer goes to the damage dealer", order.FirstOrDefault() == "Dps",
          string.Join(" > ", order));

    Check("then the tank, then the healer", order.Take(3).SequenceEqual(new[] { "Dps", "Tank", "Healer" }),
          string.Join(" > ", order));
}

{
    // A healer who is much further behind still waits, because the role order is a gate rather than
    // one term among several. This is the case the old weights got wrong: a big enough gain used to
    // jump the line, and no setting could stop it.
    var behind = Member("Healer",
                        (GearSlot.Body, GearSource.Raid), (GearSlot.Legs, GearSource.Raid),
                        (GearSlot.Head, GearSource.Raid), (GearSlot.Hands, GearSource.Raid));

    var ahead = Member("Dps", (GearSlot.Body, GearSource.Raid));

    // The healer is first in the player order, so with the loot funnelled they would take the body
    // coffer on every count except role. That is what the gate is for.
    List<PlayerPlan> Pair() => [Plan(behind, RaidRole.Healer, tier, 0), Plan(ahead, RaidRole.Dps, tier, 1)];

    var gated = new WeekSimulator(tier, new PriorityRules { Spread = 0.0 }, 12).Run(Pair());

    var first = gated.Awards.First(a => a.Slot == GearSlot.Body);
    Check("a healer first in the player order still waits behind a damage dealer",
          first.PlayerName == "Dps", $"went to {first.PlayerName}");

    // Turned off, the player order carries it.
    var noRoles = new PriorityRules { Spread = 0.0, UseRoleOrder = false };
    var loose = new WeekSimulator(tier, noRoles, 12).Run(Pair());

    Check("with the role order off, the player order decides instead",
          loose.Awards.First(a => a.Slot == GearSlot.Body).PlayerName == "Healer");
}

// --- one rule, so the plan and the chest cannot disagree -----------------------------------------

{
    // The bug this replaced: the loot window ranked by running a simulation per candidate, the
    // projection used a rule of its own, and the same coffer named two different people. Both go
    // through DropOrder now, so a ranking and a simulated week have to agree on the first name.
    var a = Member("A", (GearSlot.Body, GearSource.Raid), (GearSlot.Legs, GearSource.Raid));
    var b = Member("B", (GearSlot.Body, GearSource.Raid));
    var c = Member("C", (GearSlot.Body, GearSource.Raid), (GearSlot.Head, GearSource.Raid));

    foreach (var spread in new[] { 0.0, 0.5, 1.0 })
    {
        var policy = new PriorityRules { Spread = spread };

        List<PlayerPlan> Three() =>
        [
            Plan(a, RaidRole.Dps, tier, 0), Plan(b, RaidRole.Dps, tier, 1), Plan(c, RaidRole.Dps, tier, 2),
        ];

        var contenders = Three()
                         .Where(p => p.Wants(GearSlot.Body))
                         .Select(p => new Contender(p.Key, p.Role, p.Order, p.ItemsReceived, p.Open.Count))
                         .ToList();

        var ranked = DropOrder.Rank(policy, contenders)[0].Who.Key;
        var simulated = new WeekSimulator(tier, policy, 12).Run(Three())
                        .Awards.First(x => x.Slot == GearSlot.Body).PlayerKey;

        Check($"ranking and simulation name the same player at spread {spread:0.0}",
              ranked == simulated, $"{ranked} vs {simulated}");
    }
}

// --- a shield comes with the weapon ----------------------------------------------------------------

{
    // Only one job carries a shield and it always arrives with the weapon, out of the same coffer
    // and the same eight books. Tracking it as its own slot would invent work that does not exist.
    Check("a shield is not a tracked slot", !Slots.All.Contains(GearSlot.OffHand));
    Check("a shield files under the weapon coffer", Slots.CofferSlot(GearSlot.OffHand) == GearSlot.Weapon);

    var m = Member("Pld", (GearSlot.Weapon, GearSource.Raid), (GearSlot.OffHand, GearSource.Raid));
    var plan = Plan(m, RaidRole.Tank, tier);

    Check("a paladin owes one piece, not two", plan.Open.Count == 1,
          string.Join(", ", plan.Open.Select(n => n.Describe())));

    Check("and it is the weapon", plan.Open.Count == 1 && plan.Open[0].Slot == GearSlot.Weapon);

    // Raid gear is unique, so a character can only ever wear one raid ring — the other ring is
    // normally the augmented one. A set claiming two must not have the planner chase two coffers.
    var ringer = Member("B", (GearSlot.Ring1, GearSource.Raid), (GearSlot.Ring2, GearSource.Raid));
    Check("two raid rings count as one, since raid gear is unique",
          Plan(ringer, RaidRole.Dps, tier).Open.Count == 1);

    // The usual pairing, which is two separate pieces of work.
    var mixed = Member("C", (GearSlot.Ring1, GearSource.Raid), (GearSlot.Ring2, GearSource.TomeAugmented));
    var mixedPlan = Plan(mixed, RaidRole.Dps, tier);
    Check("a raid ring plus an augmented ring is two needs", mixedPlan.Open.Count == 2,
          string.Join(", ", mixedPlan.Open.Select(n => n.Describe())));

    // And the drop is spoken of as one thing, because there is one ring coffer.
    Check("a ring coffer is called \"Ring\"", GearSlot.Ring1.CofferLabel() == "Ring" &&
                                              GearSlot.Ring2.CofferLabel() == "Ring");
}

// --- scarcity: nobody gets starved ----------------------------------------------------------------

{
    // One accessory coffer a week out of a pool of four, two players wanting all four.
    var scarce = Tier();
    scarce.Encounter(1)!.DropCount = 1;

    var accessories = new[]
    {
        (GearSlot.Earrings, GearSource.Raid), (GearSlot.Necklace, GearSource.Raid),
        (GearSlot.Bracelets, GearSource.Raid), (GearSlot.Ring1, GearSource.Raid),
    };

    var a = Member("A", accessories);
    var b = Member("B", accessories);

    var result = new WeekSimulator(scarce, rules, 20).Run(
        [Plan(a, RaidRole.Dps, scarce), Plan(b, RaidRole.Dps, scarce)]);

    var weekA = result.FinishWeeks["A@Test"];
    var weekB = result.FinishWeeks["B@Test"];

    Check("both players finish under scarcity", !result.BeyondHorizon(result.LastFinishWeek),
          $"A W{weekA}, B W{weekB}");

    Check("neither player is starved while the other is served", Math.Abs(weekA - weekB) <= 2,
          $"A W{weekA}, B W{weekB}");
}

// --- picking up after a week decided elsewhere ----------------------------------------------------

{
    // The coming week is decided by the full ranking, not by the greedy rule in the simulator, so
    // the projection has to start after it rather than repeat it.
    var m = Member("A", (GearSlot.Body, GearSource.Raid), (GearSlot.Legs, GearSource.Raid));
    var plan = Plan(m, RaidRole.Dps, tier);

    var result = new WeekSimulator(tier, rules, 12).Run([plan], startWeek: 2);

    Check("nothing is handed out for a week that is already decided",
          result.Awards.All(a => a.Week >= 2),
          string.Join(", ", result.Awards.Select(a => $"W{a.Week}")));
}

{
    // Someone the caller already finished counts as done that week, not before the tier started.
    var m = Member("A", (GearSlot.Body, GearSource.Raid));
    m.NeedFor(GearSlot.Body).Obtained = true;

    var result = new WeekSimulator(tier, rules, 12).Run([Plan(m, RaidRole.Dps, tier)], startWeek: 2);
    Check("a plan handed over already finished is dated to the week before the run",
          result.LastFinishWeek == 1, $"week {result.LastFinishWeek}");
}

// --- determinism ---------------------------------------------------------------------------------

{
    var members = Enumerable.Range(1, 8)
                            .Select(i => Member($"P{i}", (GearSlot.Body, GearSource.Raid),
                                                (GearSlot.Legs, GearSource.TomeAugmented)))
                            .ToList();

    string RunOnce()
    {
        var plans = members.Select(m => Plan(m, RaidRole.Dps, tier)).ToList();
        var result = new WeekSimulator(tier, rules, 12).Run(plans);
        return string.Join("|", result.Awards.Select(a => $"{a.Week}:{a.What}:{a.PlayerName}"));
    }

    Check("the same input gives the same plan twice", RunOnce() == RunOnce());
}

// --- an empty roster does not throw ---------------------------------------------------------------

{
    var result = new WeekSimulator(tier, rules, 8).Run([]);
    Check("an empty roster is finished at week 0", result.LastFinishWeek == 0);
}

// --- the damage model ----------------------------------------------------------------------------

// The level constants, first. Two of the three are read from ParamGrow in the plugin; these are the
// same numbers written down so a pure test needs no excel reader, and the probe confirmed all three.

{
    Check("level 100 is 440 / 420 / 2780",
          LevelTable.Known(100) is { Main: 440, Sub: 420, Div: 2780 });

    Check("level 90 is 390 / 400 / 1900",
          LevelTable.Known(90) is { Main: 390, Sub: 400, Div: 1900 });

    Check("a level nobody raids at gets no table", LevelTable.Known(93) == null);

    Check("and no table means no estimate rather than a borrowed one",
          LevelTable.For(93, 420, 2780) == null);

    Check("a table needs the game's numbers too", LevelTable.For(100, 0, 0) == null);
}

{
    var level = LevelTable.Known(100)!.Value;

    // A substat carrying nothing sits exactly at SUB, and there the recast is the untouched 2.50 s.
    // The probe read 420 for direct hit, skill speed and spell speed alike on a set with none of
    // them, which is the same statement from the other direction.
    Check("no speed at all is a 2.50 second recast", Math.Abs(DamageModel.Recast(420, level) - 2.50) < 0.001,
          DamageModel.Recast(420, level).ToString("0.00"));

    Check("speed shortens the recast", DamageModel.Recast(2000, level) < 2.50,
          DamageModel.Recast(2000, level).ToString("0.00"));

    Check("and never lengthens it going up",
          DamageModel.Recast(1000, level) <= DamageModel.Recast(420, level) &&
          DamageModel.Recast(3000, level) <= DamageModel.Recast(1000, level));

    // Truncated to hundredths, which is why a recast is always something like 2.47 and never 2.4713.
    var recast = DamageModel.Recast(1837, level);
    Check("the recast lands on a hundredth", Math.Abs((recast * 100) - Math.Round(recast * 100)) < 1e-9,
          recast.ToString("0.0000"));
}

{
    var level = LevelTable.Known(100)!.Value;
    var profile = JobProfile.Default("PLD", magical: false, tank: true);

    // Shaped like the paladin the stat probe was run on: level 100, job modifier 100, and the
    // substats it measured.
    var stats = new StatBlock(
        Level: 100, JobModifier: 100, MainStat: 6278, WeaponDamage: 150, WeaponDelayMs: 2240,
        CriticalHit: 3016, DirectHit: 420, Determination: 2700,
        SkillSpeed: 420, SpellSpeed: 420, Tenacity: 1005);

    var estimate = DamageModel.Estimate(stats, profile, level);
    Check("a full stat block estimates", estimate != null);

    var value = estimate!.Value;
    Check("and says so when the job has no rotation profile", value.IsEstimated, value.Caveat ?? "no caveat");
    Check("the recast comes out of the same block", Math.Abs(value.Gcd - 2.50) < 0.001);

    // Bands, not equalities. The absolute scale is the one part of this that cannot be settled
    // without holding it against a gear planner, so these are wide enough not to be brittle and
    // narrow enough to catch the mistake this formula actually makes: a stray factor of a hundred.
    // The first version had one, and a paladin came out at 1.2 million damage per second.
    Check("a geared tank's 100 potency lands in the thousands",
          value.DamagePer100Potency is > 3000 and < 15000,
          value.DamagePer100Potency.ToString("0"));

    Check("and their dps in the tens of thousands at most",
          value.EstimatedDps is > 5000 and < 30000,
          value.EstimatedDps.ToString("0"));

    // Monotonicity. These are what catch a sign error or a swapped numerator, which is the way this
    // formula goes wrong — not by being subtly off, but by being backwards in one term.
    double Per100(StatBlock s) => DamageModel.Estimate(s, profile, level)!.Value.DamagePer100Potency;

    Check("more critical hit is more damage", Per100(stats with { CriticalHit = 3500 }) > Per100(stats));
    Check("more determination is more damage", Per100(stats with { Determination = 3000 }) > Per100(stats));
    Check("more weapon damage is more damage", Per100(stats with { WeaponDamage = 160 }) > Per100(stats));
    Check("more main stat is more damage", Per100(stats with { MainStat = 6500 }) > Per100(stats));
    Check("more direct hit is more damage", Per100(stats with { DirectHit = 1000 }) > Per100(stats));
    Check("more tenacity is more damage for a tank", Per100(stats with { Tenacity = 1500 }) > Per100(stats));

    // Speed does not touch the damage of one hit — it buys more hits, which is the DPS number's job.
    Check("speed does not change the damage of a hit",
          Math.Abs(Per100(stats with { SkillSpeed = 1500 }) - Per100(stats)) < 1e-9);

    var faster = DamageModel.Estimate(stats with { SkillSpeed = 1500 }, profile, level)!.Value;
    Check("but it does raise the dps", faster.EstimatedDps > value.EstimatedDps,
          $"{value.EstimatedDps:0} -> {faster.EstimatedDps:0}");

    Check("and shortens the recast", faster.Gcd < value.Gcd, $"{value.Gcd:0.00} -> {faster.Gcd:0.00}");
}

{
    var level = LevelTable.Known(100)!.Value;

    // Tenacity is a tank stat. On anybody else the stat sits at its base and the term would shave a
    // fraction off for no reason, so it is not applied at all.
    var stats = new StatBlock(100, 115, 6000, 150, 3120, 3000, 1500, 2500, 420, 420, 420);

    var caster = JobProfile.Default("BLM", magical: true, tank: false);
    var tank = JobProfile.Default("PLD", magical: false, tank: true);

    Check("a caster's recast follows spell speed, not skill speed",
          Math.Abs(DamageModel.Estimate(stats with { SpellSpeed = 1500 }, caster, level)!.Value.Gcd -
                   DamageModel.Estimate(stats, caster, level)!.Value.Gcd) > 0.001);

    Check("and skill speed does nothing for them",
          Math.Abs(DamageModel.Estimate(stats with { SkillSpeed = 1500 }, caster, level)!.Value.Gcd -
                   DamageModel.Estimate(stats, caster, level)!.Value.Gcd) < 1e-9);

    Check("tenacity below its base does not penalise a non-tank",
          DamageModel.Estimate(stats with { Tenacity = 0 }, caster, level)!.Value.DamagePer100Potency ==
          DamageModel.Estimate(stats, caster, level)!.Value.DamagePer100Potency);

    Check("but it does count for a tank",
          DamageModel.Estimate(stats with { Tenacity = 0 }, tank, level)!.Value.DamagePer100Potency <
          DamageModel.Estimate(stats, tank, level)!.Value.DamagePer100Potency);
}

{
    var level = LevelTable.Known(100)!.Value;
    var profile = JobProfile.Default("SAM", magical: false, tank: false);

    // Half a stat block is worse than none: an estimate built on a missing weapon would rate
    // somebody far below where they are and nothing on screen would say why.
    var complete = new StatBlock(100, 112, 6000, 150, 2960, 3000, 1500, 2500, 700, 420, 420);

    Check("no weapon means no estimate",
          DamageModel.Estimate(complete with { WeaponDamage = 0 }, profile, level) == null);

    Check("no main stat means no estimate",
          DamageModel.Estimate(complete with { MainStat = 0 }, profile, level) == null);

    Check("no job modifier means no estimate",
          DamageModel.Estimate(complete with { JobModifier = 0 }, profile, level) == null);

    Check("a complete one does", DamageModel.Estimate(complete, profile, level) != null);
}

// --- against Etro, on a real set -----------------------------------------------------------------

{
    // etro.gg/gearset/d12038d5-d734-41a7-ab72-148f10bc871d — "2.45 GCD Caster friendly", BLM, i790.
    //
    // This is the fixture the whole model is anchored on, and it earned its place: every term
    // matched Etro's published intermediates the first time, and the total was 23% low. The missing
    // factor was exactly 1.30000 — the job's damage trait, which had been left at 1.0 for every job.
    // A number 23% low looks perfectly plausible. That is why an outside source is worth more than
    // any amount of internal consistency.
    var level = LevelTable.Known(100)!.Value;

    var blm = new JobProfile("BLM",
                             PotencyPerSecond: 212, ReferenceGcd: 2.50, GcdShare: 0.91,
                             UsesSpellSpeed: true, UsesTenacity: false,
                             Trait: 1.30, AttackPowerMultiplier: 237);

    var set = new StatBlock(
        Level: 100, JobModifier: 115, MainStat: 6837, WeaponDamage: 158, WeaponDelayMs: 3280,
        CriticalHit: 3548, DirectHit: 1837, Determination: 2341,
        SkillSpeed: 420, SpellSpeed: 787, Tenacity: 420);

    var estimate = DamageModel.Estimate(set, blm, level)!.Value;

    Check("Etro's set: 13161.4 damage per 100 potency",
          Math.Abs(estimate.DamagePer100Potency - 13161.4) < 0.1,
          estimate.DamagePer100Potency.ToString("0.0"));

    Check("Etro's set: a 2.45 second recast", Math.Abs(estimate.Gcd - 2.45) < 0.001,
          estimate.Gcd.ToString("0.00"));

    // The intermediates Etro publishes alongside it, each one its own chance to be wrong.
    Check("Etro's set: the trait is what closes the gap",
          Math.Abs(DamageModel.Estimate(set, blm with { Trait = 1.0 }, level)!.Value.DamagePer100Potency
                   - (13161.4 / 1.30)) < 0.1);

    Check("a magical job's trait is 1.30",
          Math.Abs(JobProfile.TraitFor(magical: true, tank: false) - 1.30) < 1e-9);
}

{
    // etro.gg/gearset/1dde8dd8-0953-4760-a5a4-1710a023f064 — paladin, i790, 2.50 GCD.
    //
    // The second anchor, and it corrected the first. A physical trait of 1.20 was the conventional
    // reading and this set says 1.00000 exactly — so every physical job had been overstated by a
    // fifth. It also settles the tank attack power multiplier at 190, which had been a guess: no
    // other value reproduces Etro's strength multiplier of 2834%.
    var level = LevelTable.Known(100)!.Value;

    var pld = new JobProfile("PLD",
                             PotencyPerSecond: 169, ReferenceGcd: 2.50, GcdShare: 0.77,
                             UsesSpellSpeed: false, UsesTenacity: true,
                             Trait: 1.00, AttackPowerMultiplier: 190);

    var set = new StatBlock(
        Level: 100, JobModifier: 100, MainStat: 6772, WeaponDamage: 158, WeaponDelayMs: 2240,
        CriticalHit: 3595, DirectHit: 1230, Determination: 3066,
        SkillSpeed: 420, SpellSpeed: 420, Tenacity: 622);

    var estimate = DamageModel.Estimate(set, pld, level)!.Value;

    Check("Etro's paladin set: 7979.5 damage per 100 potency",
          Math.Abs(estimate.DamagePer100Potency - 7979.5) < 0.1,
          estimate.DamagePer100Potency.ToString("0.0"));

    Check("Etro's paladin set: a 2.50 second recast", Math.Abs(estimate.Gcd - 2.50) < 0.001);

    Check("a tank's trait is 1.00",
          Math.Abs(JobProfile.TraitFor(magical: false, tank: true) - 1.00) < 1e-9);

    // No other multiplier reproduces Etro's figure, which is what makes 190 a measurement.
    Check("only 190 gives a tank Etro's strength multiplier",
          Math.Abs(DamageModel.Estimate(set, pld with { AttackPowerMultiplier = 237 }, level)!
                       .Value.DamagePer100Potency - 7979.5) > 100);
}

{
    // etro.gg/gearset/3487d0fa-e0e3-4d55-975f-f9843a021cc6 — dancer, i790, 2.50 GCD.
    //
    // The third anchor, and it settles the shape of the whole table: this is a **tank exception**,
    // not the magical-against-physical split it looked like after two sets. A physical job that is
    // not a tank carries 1.20 and the full 237 — which was the original guess, wrong only for tanks.
    var level = LevelTable.Known(100)!.Value;

    var dnc = new JobProfile("DNC",
                             PotencyPerSecond: 265.6, ReferenceGcd: 2.50, GcdShare: 0.47,
                             UsesSpellSpeed: false, UsesTenacity: false,
                             Trait: 1.20, AttackPowerMultiplier: 237);

    var set = new StatBlock(
        Level: 100, JobModifier: 115, MainStat: 6841, WeaponDamage: 158, WeaponDelayMs: 3120,
        CriticalHit: 3549, DirectHit: 2035, Determination: 2509,
        SkillSpeed: 420, SpellSpeed: 420, Tenacity: 420);

    var estimate = DamageModel.Estimate(set, dnc, level)!.Value;

    Check("Etro's dancer set: 12367.4 damage per 100 potency",
          Math.Abs(estimate.DamagePer100Potency - 12367.43) < 0.1,
          estimate.DamagePer100Potency.ToString("0.00"));

    Check("physical ranged is its own trait at 1.20",
          Math.Abs(JobProfile.TraitForPhysicalRanged() - 1.20) < 1e-9);
}

{
    // etro.gg/gearset/e76a9c0f-c41c-433f-b1cc-d75c3f86b39a — dragoon, i790, 2.50 GCD.
    //
    // The fourth anchor, and it broke the third rule. "Tank exception" was the story after the dancer
    // set; a melee reads 1.00 like the tank, so **physical ranged** is the odd one out. Four sets,
    // four categories, one measurement each — and each of the first three suggested a rule the next
    // one disproved.
    var level = LevelTable.Known(100)!.Value;

    var drg = new JobProfile("DRG",
                             PotencyPerSecond: 249, ReferenceGcd: 2.50, GcdShare: 0.58,
                             UsesSpellSpeed: false, UsesTenacity: false,
                             Trait: 1.00, AttackPowerMultiplier: 237);

    var set = new StatBlock(
        Level: 100, JobModifier: 115, MainStat: 6838, WeaponDamage: 158, WeaponDelayMs: 2800,
        CriticalHit: 3605, DirectHit: 1982, Determination: 2506,
        SkillSpeed: 420, SpellSpeed: 420, Tenacity: 420);

    var estimate = DamageModel.Estimate(set, drg, level)!.Value;

    Check("Etro's dragoon set: 10311.2 damage per 100 potency",
          Math.Abs(estimate.DamagePer100Potency - 10311.15) < 0.1,
          estimate.DamagePer100Potency.ToString("0.00"));

    Check("a melee's trait is 1.00, the same as a tank's",
          Math.Abs(JobProfile.TraitFor(magical: false, tank: false) - 1.00) < 1e-9);

    // Four categories, and only three distinct traits — so the table cannot be reduced to one axis.
    Check("physical ranged sits between melee and magical",
          JobProfile.TraitFor(magical: false, tank: false) <
          JobProfile.TraitForPhysicalRanged() &&
          JobProfile.TraitForPhysicalRanged() <
          JobProfile.TraitFor(magical: true, tank: false));
}

{
    // The potency figure holds at the recast it was measured at, and only the part bound to the
    // global cooldown moves away from it. This is the shape of the calibration, and getting the
    // total wrong is what put a sage 39% low against xivgear.
    var level = LevelTable.Known(100)!.Value;

    var profile = new JobProfile("SGE",
                                 PotencyPerSecond: 214, ReferenceGcd: 2.50, GcdShare: 0.81,
                                 UsesSpellSpeed: true, UsesTenacity: false,
                                 Trait: 1.30, AttackPowerMultiplier: 237);

    Check("the measured figure holds at its own recast",
          Math.Abs(profile.PotencyPerSecondAt(2.50) - 214) < 1e-9,
          profile.PotencyPerSecondAt(2.50).ToString("0.0"));

    Check("a shorter recast lands more potency", profile.PotencyPerSecondAt(2.40) > 214);
    Check("a longer one lands less", profile.PotencyPerSecondAt(2.60) < 214);

    // Only the GCD-bound share scales, so the change is smaller than the recast's own change.
    var faster = profile.PotencyPerSecondAt(2.00) / 214;
    Check("and it scales by the gcd share, not one for one", faster < 2.50 / 2.00,
          $"{faster:0.000} against {2.50 / 2.00:0.000}");

    // A job whose damage is all off-cooldown gains nothing from speed at all.
    var allOgcd = profile with { GcdShare = 0 };
    Check("no gcd share means speed buys nothing",
          Math.Abs(allOgcd.PotencyPerSecondAt(2.00) - 214) < 1e-9);

    var allGcd = profile with { GcdShare = 1 };
    Check("a full gcd share scales one for one with the recast",
          Math.Abs(allGcd.PotencyPerSecondAt(2.00) - (214 * 2.50 / 2.00)) < 1e-9);
}

{
    // A cross-check from a different source entirely. The xivgear sims report a damage per 100
    // potency for jobs whose dps solver is missing — 13398.5 for a red mage, 12402.7 for a bard — and
    // the two are the same tier with near-identical stat lines. Their ratio should therefore be the
    // ratio of their traits, and nothing else.
    //
    // Four Etro sets said 1.30 and 1.20. This says 1.0803 where 1.30/1.20 is 1.0833: two independent
    // sources agreeing to a third of a percent, which is worth more than either on its own.
    const double casterOverRanged = 13398.462 / 12402.733;
    var traitRatio = JobProfile.TraitFor(magical: true, tank: false) / JobProfile.TraitForPhysicalRanged();

    Check("the sims' damage ratio matches the trait ratio",
          Math.Abs(casterOverRanged - traitRatio) / traitRatio < 0.01,
          $"{casterOverRanged:0.0000} against {traitRatio:0.0000}");
}

{
    // Swapping one stat is how every item comparison is expressed, so it has to move the right one.
    var stats = new StatBlock(100, 100, 6000, 150, 2240, 3000, 1000, 2500, 420, 420, 1000);

    Check("a swap moves critical hit",
          stats.With(StatBlock.Attributes.CriticalHit, 100).CriticalHit == 3100);

    Check("and determination", stats.With(StatBlock.Attributes.Determination, -50).Determination == 2450);

    Check("and leaves everything else alone",
          stats.With(StatBlock.Attributes.CriticalHit, 100) with { CriticalHit = 3000 } == stats);

    Check("a stat the formula does not read changes nothing",
          stats.With(StatBlock.Attributes.Strength, 500) == stats);
}

// --- three answers to "who needs it more" ---------------------------------------------------------

{
    static string Won(PriorityRules with, params Contender[] candidates) =>
        DropOrder.Rank(with, candidates)[0].Who.Key;

    // The gains are flat damage per second, not percentages. One hundred points is worth one place
    // in the order, so 500 against 100 is four places of advantage.
    //
    // Somebody who has had four pieces and would gain a great deal, against somebody who has had
    // none and would gain a little. This is the case the three rankings genuinely disagree about,
    // and a group is entitled to either answer.
    var served = new Contender("Served", RaidRole.Dps, 0, 4, 3, DpsGain: 500);
    var empty = new Contender("Empty", RaidRole.Dps, 1, 0, 3, DpsGain: 100);

    Check("by missing gear, whoever has had least",
          Won(new PriorityRules { Spread = 1.0, Basis = NeedBasis.MissingGear }, served, empty) == "Empty");

    Check("by damage, whoever gains most",
          Won(new PriorityRules { Spread = 1.0, Basis = NeedBasis.DpsGain }, served, empty) == "Served");

    // Both, on one scale: four items won against four hundred more points of damage. Just enough.
    Check("by both, four hundred points overtakes four items won",
          Won(new PriorityRules { Spread = 1.0, Basis = NeedBasis.Both }, served, empty) == "Served");

    var closer = empty with { DpsGain = 150 };
    Check("and three hundred and fifty does not",
          Won(new PriorityRules { Spread = 1.0, Basis = NeedBasis.Both }, served, closer) == "Empty");

    // The whole reason for flat rather than percent. A healer gaining a large share of a small output
    // is fewer points of raid damage than a melee gaining a smaller share of a big one — and the
    // group only feels the points. With the role gate off so the two actually meet.
    var bigShareSmallOutput = new Contender("Healer", RaidRole.Healer, 0, 0, 3, DpsGain: 850);
    var smallShareBigOutput = new Contender("Melee", RaidRole.Dps, 1, 0, 3, DpsGain: 1300);

    Check("the bigger flat gain wins, whatever share of their own damage it is",
          Won(new PriorityRules { Spread = 1.0, Basis = NeedBasis.DpsGain, UseRoleOrder = false },
              bigShareSmallOutput, smallShareBigOutput) == "Melee");

    // The role gate still sits above all three, or the whole point of it is lost the moment somebody
    // switches the ranking over.
    var healer = new Contender("Healer", RaidRole.Healer, 0, 0, 6, DpsGain: 900);
    var dps = new Contender("Dps", RaidRole.Dps, 1, 4, 1, DpsGain: 10);

    foreach (var basis in new[] { NeedBasis.MissingGear, NeedBasis.DpsGain, NeedBasis.Both })
    {
        Check($"the role gate holds when ranking {basis}",
              Won(new PriorityRules { Spread = 1.0, Basis = basis }, healer, dps) == "Dps");
    }

    // No gain known reads as no gain, so a roster with nobody scanned ranks by the queue rather than
    // declaring everyone equally deserving.
    var blind1 = new Contender("A", RaidRole.Dps, 0, 3, 2);
    var blind2 = new Contender("B", RaidRole.Dps, 1, 0, 2);

    Check("with no damage known, the order decides",
          Won(new PriorityRules { Spread = 1.0, Basis = NeedBasis.DpsGain }, blind1, blind2) == "A");
}

// --- a ring is a ring, whichever finger it is on --------------------------------------------------

{
    const uint raidRing = 100;
    const uint tomeRing = 200;

    static RosterMember Rings(uint target1, uint target2, uint worn1, uint worn2)
    {
        var m = new RosterMember { Name = "R", World = "Test" };

        m.NeedFor(GearSlot.Ring1).BisItemId = target1;
        m.NeedFor(GearSlot.Ring2).BisItemId = target2;
        m.NeedFor(GearSlot.Ring1).EquippedItemId = worn1;
        m.NeedFor(GearSlot.Ring2).EquippedItemId = worn2;

        return m;
    }

    // Both target rings, worn the other way round. Compared slot for slot this is two misses on a
    // finished pair — which would keep two slots in the distribution and show a gain for a piece
    // already owned. Which finger a ring is on carries no information.
    var crossed = Rings(raidRing, tomeRing, tomeRing, raidRing);
    Check("a crossed pair is misread before aligning",
          !crossed.NeedFor(GearSlot.Ring1).IsWearingTarget &&
          !crossed.NeedFor(GearSlot.Ring2).IsWearingTarget);

    Check("aligning reports that it moved something", crossed.AlignRings());

    Check("and then both rings are on target",
          crossed.NeedFor(GearSlot.Ring1).IsWearingTarget &&
          crossed.NeedFor(GearSlot.Ring2).IsWearingTarget);

    // A pair already in order is left alone, or aligning would flap back and forth every scan.
    var straight = Rings(raidRing, tomeRing, raidRing, tomeRing);
    Check("a pair already in order is not touched", !straight.AlignRings());
    Check("and stays on target",
          straight.NeedFor(GearSlot.Ring1).IsWearingTarget &&
          straight.NeedFor(GearSlot.Ring2).IsWearingTarget);

    // One of the two, on the other finger: crossing gets one match instead of none.
    var half = Rings(raidRing, tomeRing, 0, raidRing);
    Check("one ring on the wrong finger is still moved", half.AlignRings());
    Check("and lands on its own target", half.NeedFor(GearSlot.Ring1).IsWearingTarget);

    // Nothing matching either way round keeps the order the game reported — there is no reason to
    // prefer one arrangement, and moving it would make a scan's output depend on the last scan.
    var neither = Rings(raidRing, tomeRing, 300, 400);
    Check("a pair matching nothing is left as read", !neither.AlignRings());
    Check("and the order is unchanged", neither.NeedFor(GearSlot.Ring1).EquippedItemId == 300);

    // The classification travels with the item, or a swapped pair would keep the other's source.
    var sources = Rings(raidRing, tomeRing, tomeRing, raidRing);
    sources.NeedFor(GearSlot.Ring1).EquippedSource = GearSource.TomeAugmented;
    sources.NeedFor(GearSlot.Ring2).EquippedSource = GearSource.Raid;
    sources.AlignRings();

    Check("the source follows the ring",
          sources.NeedFor(GearSlot.Ring1).EquippedSource == GearSource.Raid &&
          sources.NeedFor(GearSlot.Ring2).EquippedSource == GearSource.TomeAugmented);
}

// --- swapping one piece for another --------------------------------------------------------------

{
    const uint crt = StatBlock.Attributes.CriticalHit;
    const uint det = StatBlock.Attributes.Determination;
    const uint dh = StatBlock.Attributes.DirectHitRate;
    const uint str = StatBlock.Attributes.Strength;

    List<StatChange> Piece(params (uint Stat, int Value)[] stats) =>
        stats.Select(s => new StatChange(s.Stat, s.Value)).ToList();

    // A straight upgrade: same stats, more of them.
    var worse = Piece((str, 400), (crt, 300), (det, 200));
    var better = Piece((str, 450), (crt, 340), (det, 230));

    var upgrade = GearDelta.Between(worse, better);
    Check("a straight upgrade is all gains", upgrade.All(c => c.Delta > 0),
          string.Join(", ", upgrade.Select(c => $"[{c.BaseParam}]{c.Delta:+0;-0}")));

    // A sidegrade. This is the case that goes wrong quietly: counting only what the new piece has
    // makes losing a stat invisible, and every sidegrade then reads as a pure gain.
    var sideways = GearDelta.Between(Piece((str, 400), (crt, 300)), Piece((str, 400), (det, 300)));

    Check("a sidegrade counts the stat that was lost",
          sideways.Any(c => c.BaseParam == crt && c.Delta == -300),
          string.Join(", ", sideways.Select(c => $"[{c.BaseParam}]{c.Delta:+0;-0}")));

    Check("and the one that was gained", sideways.Any(c => c.BaseParam == det && c.Delta == 300));
    Check("and leaves what did not move at zero", sideways.Any(c => c.BaseParam == str && c.Delta == 0));

    // An empty slot: everything the new piece has is a gain, which is what a first-week coffer is.
    var fromNothing = GearDelta.Between([], better);
    Check("filling an empty slot gains the whole piece",
          fromNothing.Count == 3 && fromNothing.All(c => c.Delta > 0));

    // --- melds ---------------------------------------------------------------------------------
    //
    // Materia has to land on the same entry as the stat it strengthens. Two entries for one stat and
    // Between() counts whichever it meets first, which is a silent wrong answer rather than a crash.
    var melded = GearDelta.Plus(Piece((str, 400), (crt, 300)), Piece((crt, 54), (det, 36)));

    Check("a materia adds to a stat the piece already has",
          melded.Count(c => c.BaseParam == crt) == 1 &&
          melded.Single(c => c.BaseParam == crt).Delta == 354);

    Check("and one the piece does not have is added",
          melded.Single(c => c.BaseParam == det).Delta == 36);

    // The case the user's meld rules single out, and the reason both sides have to carry their own
    // materia: crafted gear takes five, raid gear exactly two. Traded slot for slot the raid piece is
    // the better item and still loses substats — counting only the items would call it a pure gain.
    var crafted = GearDelta.Plus(Piece((str, 468), (crt, 296), (det, 207)),
                                 Piece((crt, 54), (det, 54), (dh, 162)));

    var raid = GearDelta.Plus(Piece((str, 477), (crt, 333), (dh, 233)), Piece((crt, 54), (det, 54)));

    var traded = GearDelta.Between(crafted, raid);

    Check("trading five melds for two is not a pure gain", traded.Any(c => c.Delta < 0),
          string.Join(", ", traded.Select(c => $"[{c.BaseParam}]{c.Delta:+0;-0}")));

    Check("the main stat still goes up", traded.Single(c => c.BaseParam == str).Delta == 9);

    // Melds that do not move must cancel exactly, or every comparison drifts by whatever is in them.
    var samePiece = GearDelta.Plus(better, Piece((crt, 54), (dh, 54)));
    Check("identical melds on both sides cancel",
          GearDelta.Between(samePiece, samePiece).All(c => c.Delta == 0));
}

{
    var level = LevelTable.Known(100)!.Value;
    var pld = JobProfile.Default("PLD", magical: false, tank: true);

    var stats = new StatBlock(100, 100, 6278, 150, 2240, 3016, 420, 2700, 420, 420, 1005);

    // The main stat is job-dependent, and this is the check that a strength ring is worth nothing to
    // a black mage. Routing it by name instead of by the job's own primary stat would have every
    // caster valuing tank gear.
    var withStrength = GearDelta.Apply(stats, primaryStat: StatBlock.Attributes.Strength,
                                       [new StatChange(StatBlock.Attributes.Strength, 200)]);

    Check("a paladin's main stat moves with strength", withStrength.MainStat == 6478);

    var casterStats = GearDelta.Apply(stats, primaryStat: StatBlock.Attributes.Intelligence,
                                      [new StatChange(StatBlock.Attributes.Strength, 200)]);

    Check("and a caster's does not", casterStats.MainStat == 6278);

    // A weapon carries damage and delay, which are not stats and would otherwise be dropped.
    var rearmed = GearDelta.Apply(stats, StatBlock.Attributes.Strength, [], weaponDamage: 158, weaponDelayMs: 2960);
    Check("a weapon swap carries its damage", rearmed.WeaponDamage == 158);
    Check("and its delay", rearmed.WeaponDelayMs == 2960);

    var before = DamageModel.Estimate(stats, pld, level)!.Value;
    var after = DamageModel.Estimate(rearmed, pld, level)!.Value;
    var gain = new GearGain(before, after);

    Check("a better weapon is an upgrade", gain.IsUpgrade, $"{gain.Percent:+0.00;-0.00;0.00}%");
    Check("the percentage is signed the same way as the dps",
          Math.Sign(gain.Percent) == Math.Sign(gain.Dps));

    var downgrade = new GearGain(after, before);
    Check("and the other way round is not an upgrade", !downgrade.IsUpgrade,
          $"{downgrade.Percent:+0.00;-0.00;0.00}%");

    Check("no change is no gain", Math.Abs(new GearGain(before, before).Percent) < 1e-9);
}

// --- the tomestone ledger ------------------------------------------------------------------------

{
    int TomeCost(GearSlot slot) => tier.TomeCostForSlot(slot) ?? 0;

    Check("body and legs cost 825", TomeCost(GearSlot.Body) == 825 && TomeCost(GearSlot.Legs) == 825);
    Check("head, hands and feet cost 495",
          TomeCost(GearSlot.Head) == 495 && TomeCost(GearSlot.Hands) == 495 && TomeCost(GearSlot.Feet) == 495);
    Check("accessories cost 375",
          TomeCost(GearSlot.Earrings) == 375 && TomeCost(GearSlot.Ring1) == 375 && TomeCost(GearSlot.Ring2) == 375);
    Check("the weapon costs 500", TomeCost(GearSlot.Weapon) == 500);

    // A material is bought with books and has no tomestone price at all. Seeding one would put a
    // second, wrong way to pay for it into the arithmetic.
    Check("upgrade materials have no tome price",
          tier.CostRules.Where(r => r.Upgrade != null).All(r => r.TomeCost == 0));

    // Typed-in numbers are somebody's decision, the same rule the book costs already follow.
    var typed = Tier();
    typed.CostRules.First(r => r.Slots.Contains(GearSlot.Body)).TomeCost = 900;
    typed.SeedTomeCosts();
    Check("a price somebody typed in is left alone", typed.TomeCostForSlot(GearSlot.Body) == 900);

    // A rule spanning two categories cannot be priced by either, and saying nothing is the only
    // honest answer — it would have to be split first.
    var mixed = new TierDefinition();
    mixed.CostRules.Add(new TierCostRule { Label = "Mixed", Slots = [GearSlot.Body, GearSlot.Head] });
    mixed.SeedTomeCosts();
    Check("a rule mixing categories is left unpriced", mixed.TomeCostForSlot(GearSlot.Body) == null);
}

{
    var member = Member("Tomes",
                        (GearSlot.Head, GearSource.Tome),
                        (GearSlot.Hands, GearSource.Tome),
                        (GearSlot.Body, GearSource.TomeAugmented),
                        (GearSlot.Weapon, GearSource.Raid));

    var plan = Plan(member, RaidRole.Dps, tier);

    Check("the bank starts at a week's worth per week before the tier",
          plan.Tomes == tier.TomestonesPerWeek * tier.PriorTomeWeeks, $"{plan.Tomes}");

    Check("both tomestone sources are on the shopping list", plan.TomeOpen.Count == 3,
          string.Join(", ", plan.TomeOpen.Select(n => $"{n.Slot}:{n.Cost}")));

    Check("and the raid weapon is not", plan.TomeOpen.All(n => n.Slot != GearSlot.Weapon));

    Check("the total is what the categories say", TomeLedger.Outstanding(plan) == 495 + 495 + 825,
          $"{TomeLedger.Outstanding(plan)}");

    // Only the augmented one is a material's prerequisite; a plain tome piece blocks nothing.
    Check("only the augmented piece is flagged as a material's base",
          plan.TomeOpen.Count(n => n.ForAugment) == 1);

    member.NeedFor(GearSlot.Body).BaseObtained = true;
    var bought = Plan(member, RaidRole.Dps, tier);

    Check("a piece already bought is off the list", bought.TomeOpen.Count == 2);
    Check("and out of the total", TomeLedger.Outstanding(bought) == 990);
}

{
    // The week the last purchase can land, from the closed form. The simulator arrives at the same
    // number by spending week by week, and T1 asserts the two agree.
    var member = Member("Slow",
                        (GearSlot.Body, GearSource.Tome),
                        (GearSlot.Legs, GearSource.Tome),
                        (GearSlot.Head, GearSource.Tome));

    var plan = Plan(member, RaidRole.Dps, tier);   // 825 + 825 + 495 = 2145, bank 450

    Check("the tome clock is the shortfall over the weekly cap",
          TomeLedger.WeekAffordedBy(tier, plan) == 4, $"W{TomeLedger.WeekAffordedBy(tier, plan)}");

    plan.Tomes = 3000;
    Check("enough in the bank already is week one, not week zero",
          TomeLedger.WeekAffordedBy(tier, plan) == 1);

    plan.TomeOpen.Clear();
    Check("owing nothing is week zero", TomeLedger.WeekAffordedBy(tier, plan) == 0);
}

{
    // The gate. A twine is only worth handing over if the piece under it can be worn.
    var member = Member("Augmenting", (GearSlot.Body, GearSource.TomeAugmented));
    var plan = Plan(member, RaidRole.Dps, tier);

    plan.Tomes = 0;
    Check("a material is no use without the piece under it", !plan.CanUseUpgrade(GearSide.Left));

    plan.Tomes = 825;
    Check("and is the moment the piece is affordable", plan.CanUseUpgrade(GearSide.Left));

    member.NeedFor(GearSlot.Body).BaseObtained = true;
    var owns = Plan(member, RaidRole.Dps, tier);
    owns.Tomes = 0;
    Check("owning the piece outright needs no tomestones at all", owns.CanUseUpgrade(GearSide.Left));

    Check("and says nothing about a side they do not want", !owns.CanUseUpgrade(GearSide.Right));
}

{
    // The stopgap weapon: not in anybody's target set, and 500 out of their budget all the same.
    var member = Member("Stopgap", (GearSlot.Weapon, GearSource.Raid), (GearSlot.Head, GearSource.Tome));

    var before = TomeLedger.Outstanding(Plan(member, RaidRole.Dps, tier));

    member.WeaponTokenObtained = true;
    var after = Plan(member, RaidRole.Dps, tier);

    Check("taking the weapon stone puts 500 on the bill",
          TomeLedger.Outstanding(after) == before + 500, $"{before} -> {TomeLedger.Outstanding(after)}");

    // A player whose target weapon really is the augmented tome one already owes for it once.
    var augmented = Member("Aims for it", (GearSlot.Weapon, GearSource.TomeAugmented));
    augmented.WeaponTokenObtained = true;

    Check("and is not charged twice when the weapon was already on the list",
          Plan(augmented, RaidRole.Dps, tier).TomeOpen.Count(n => n.Slot == GearSlot.Weapon) == 1);
}

// --- the simulator spends tomestones -------------------------------------------------------------

{
    SimulationResult RunFor(params PlayerPlan[] plans) =>
        new WeekSimulator(tier, rules, 20).Run(plans);

    // Nothing but tomestone gear: no coffer is owed, and the raid clock says done in week zero
    // while the set is still four weeks off. That gap is the whole reason for the second clock.
    var vendor = Member("Vendor", (GearSlot.Body, GearSource.Tome), (GearSlot.Legs, GearSource.Tome),
                        (GearSlot.Head, GearSource.Tome));

    var plan = Plan(vendor, RaidRole.Dps, tier);
    var expected = TomeLedger.WeekAffordedBy(tier, plan);
    var run = RunFor(plan);

    Check("the raid owes them nothing at all", run.FinishWeeks[vendor.Key] == 0,
          $"W{run.FinishWeeks[vendor.Key]}");

    Check("and the set is finished when the last piece is paid for",
          run.WholeSetWeek(vendor.Key) == expected, $"W{run.WholeSetWeek(vendor.Key)} vs W{expected}");

    // The two routes to that week are independent - one counts weeks of income, the other spends
    // week by week in whatever order it likes. They have to agree, and a disagreement is a bug in
    // one of them rather than a matter of taste.
    Check("the closed form and the simulation agree", expected == 4, $"W{expected}");

    Check("every purchase is priced", run.Awards.Where(a => a.WithTomestones)
                                                .All(a => a.TomeCost is 825 or 495 or 375 or 500));

    Check("and nothing is bought twice",
          run.Awards.Count(a => a.WithTomestones) == 3, $"{run.Awards.Count(a => a.WithTomestones)}");
}

{
    // The whole set is done when both halves are: still owed a coffer means not finished, however
    // long ago the last tomestone piece was paid for.
    var mixed = Member("Mixed", (GearSlot.Head, GearSource.Tome), (GearSlot.Body, GearSource.Raid));
    var plan = Plan(mixed, RaidRole.Dps, tier);

    // Nobody to compete with, so the body coffer lands in week one and the head is affordable at
    // once - both clocks come out at one, and neither can come out earlier.
    var run = new WeekSimulator(tier, rules, 20).Run([plan]);

    Check("the tome clock is never earlier than the raid clock",
          run.WholeSetWeek(mixed.Key) >= run.FinishWeeks[mixed.Key],
          $"W{run.FinishWeeks[mixed.Key]} / W{run.WholeSetWeek(mixed.Key)}");
}

{
    // The gate, end to end. Two players want an armour material. The one the order puts first
    // cannot buy the body piece for weeks; the other already owns theirs.
    var first = Member("First", (GearSlot.Body, GearSource.TomeAugmented),
                                (GearSlot.Legs, GearSource.TomeAugmented));

    var second = Member("Second", (GearSlot.Body, GearSource.TomeAugmented));
    second.NeedFor(GearSlot.Body).BaseObtained = true;

    var poor = Plan(first, RaidRole.Dps, tier, 0);
    poor.Tomes = 0;

    var ready = Plan(second, RaidRole.Dps, tier, 1);
    ready.Tomes = 0;

    var usable = WeekSimulator.Usable([poor, ready], GearSide.Left).ToList();

    Check("a material goes to whoever can use it, not to whoever is first in line",
          usable.Count == 1 && usable[0].Key == second.Key,
          string.Join(", ", usable.Select(p => p.Name)));

    // And it still goes out when nobody can - holding it beats leaving it in the chest.
    poor.Tomes = 0;
    var alsoPoor = Plan(second, RaidRole.Dps, tier, 1);
    alsoPoor.TomeOpen.Clear();
    alsoPoor.TomeOpen.Add(new TomeNeed(GearSlot.Body, 825, true));
    alsoPoor.Tomes = 0;

    var nobody = WeekSimulator.Usable([poor, alsoPoor], GearSide.Left).ToList();

    Check("and goes out anyway when nobody can use it yet", nobody.Count == 2,
          string.Join(", ", nobody.Select(p => p.Name)));
}

{
    // A piece with a material already sitting in the bag is bought first, even when it is the
    // cheaper one - that purchase finishes a slot the same evening, and the other only banks stats.
    var member = Member("Ordering", (GearSlot.Body, GearSource.Tome),
                                    (GearSlot.Head, GearSource.TomeAugmented));

    // The head's material has already been won, so the piece under it is all that is missing.
    member.NeedFor(GearSlot.Head).UpgradeObtained = true;

    var plan = Plan(member, RaidRole.Dps, tier);
    plan.Tomes = 900;   // both affordable once the week's 450 lands

    var run = new WeekSimulator(tier, rules, 1).Run([plan]);
    var bought = run.Awards.Where(a => a.WithTomestones).ToList();

    Check("the piece whose material is waiting is bought first",
          bought.Count > 0 && bought[0].Slot == GearSlot.Head,
          string.Join(", ", bought.Select(a => $"{a.Slot}:{a.TomeCost}")));

    Check("and the expensive one follows in the same week when it is affordable",
          bought.Count == 2 && bought[1].Slot == GearSlot.Body);
}

// --- gear the tier does not recognise --------------------------------------------------------------

{
    // "Other" exists so a relic stops reading as "Craft" on somebody's sheet. What it must not do is
    // start costing the raid something, which would put a dungeon drop into the distribution.
    Check("something unrecognised needs nothing from the raid", !GearSource.Other.NeedsRaidResource());

    Check("and is offered as a choice by hand",
          Slots.SelectableSources().Contains(GearSource.Other));

    Check("it has a label of its own", Slots.Label(GearSource.Other) == "Other");

    Check("and every source says something",
          Slots.SelectableSources().All(s => Slots.Description(s).Length > 0 || s == GearSource.None));

    var member = Member("Relic", (GearSlot.Weapon, GearSource.Other), (GearSlot.Body, GearSource.Raid));
    var plan = Plan(member, RaidRole.Dps, tier);

    Check("a slot filled by something else is not an open need",
          plan.Open.All(n => Slots.CofferSlot(n.Slot) != GearSlot.Weapon),
          string.Join(", ", plan.Open.Select(n => n.Describe())));
}

// --- the drops nothing can rank ------------------------------------------------------------------

{
    var special = Tier();

    Check("nothing is special until it is named", special.SpecialFor(999) == null);

    special.WeaponToken = new TierSpecialDrop { ItemId = 999, ItemName = "Stone", Encounter = 2 };
    special.Mount = new TierSpecialDrop { ItemId = 888, ItemName = "Mount", Encounter = 4 };

    Check("the stone is recognised", special.SpecialFor(999) == SpecialDrop.WeaponToken);
    Check("and the mount", special.SpecialFor(888) == SpecialDrop.Mount);

    // The weapon material is an ordinary need until a stone exists, and only then does it become a
    // thing that has to be decided by hand.
    Check("the weapon material follows the stone",
          special.SpecialFor(3) == SpecialDrop.WeaponAugment);

    var noStone = Tier();
    Check("and stays an ordinary need without one", noStone.SpecialFor(3) == null);

    // An armour material is never special, stone or no stone.
    Check("the armour material is never special", special.SpecialFor(2) == null);

    Check("and an id nobody named is nothing", special.SpecialFor(4242) == null);
}

// --- second characters ---------------------------------------------------------------------------

{
    var mainMember = Member("Main", (GearSlot.Body, GearSource.Raid));
    var altMember = Member("Alt", (GearSlot.Body, GearSource.Raid));
    altMember.IsAlt = true;

    var main = Plan(mainMember, RaidRole.Dps, tier, 0);
    var alt = Plan(altMember, RaidRole.Dps, tier, 1);

    Check("the plan carries the mark", alt.IsAlt && !main.IsAlt);

    var strict = new PriorityRules();
    var generous = new PriorityRules { AltsMayTakeSpareGear = true };

    Check("an alt is not in the field while a main wants it",
          WeekSimulator.Field(strict, [main, alt]).Single().Key == mainMember.Key);

    // Not a weight — a main that has won ten pieces still comes before an alt that has won none.
    main.ItemsReceived = 10;
    Check("and not even when the main has had everything",
          WeekSimulator.Field(strict, [main, alt]).Single().Key == mainMember.Key);

    Check("a coffer nobody else wants is greed by default",
          !WeekSimulator.Field(strict, [alt]).Any());

    Check("and goes on the alt when the rules allow it",
          WeekSimulator.Field(generous, [alt]).Single().Key == altMember.Key);

    // The order the two filters run in matters. Alts leave the field first, so a material an alt
    // could use is never a reason to take it off a main who cannot use it yet.
    var stuck = Plan(Member("Stuck", (GearSlot.Body, GearSource.TomeAugmented)), RaidRole.Dps, tier, 0);
    stuck.Tomes = 0;

    var readyAlt = Plan(AltWith(), RaidRole.Dps, tier, 1);
    readyAlt.Tomes = 5000;

    var field = WeekSimulator.Usable(WeekSimulator.Field(strict, [stuck, readyAlt]), GearSide.Left).ToList();

    Check("a material stays with the main even when only an alt could use it",
          field.Count == 1 && field[0].Key == stuck.Key,
          string.Join(", ", field.Select(p => p.Name)));

    static RosterMember AltWith()
    {
        var member = Member("ReadyAlt", (GearSlot.Body, GearSource.TomeAugmented));
        member.IsAlt = true;
        return member;
    }
}

// --- the version 1 migration ---------------------------------------------------------------------

{
    // A full old config: a roster, a tier, and every setting set to something other than its
    // default, so a field that is silently dropped shows up as a default rather than hiding behind
    // one.
    var oldRoster = new List<RosterMember>
    {
        Member("Astra", (GearSlot.Body, GearSource.Raid)),
        Member("Bex", (GearSlot.Legs, GearSource.TomeAugmented)),
    };

    oldRoster[0].Tokens[3] = 5;
    oldRoster[1].IsAlt = true;

    var oldTier = Tier();
    oldTier.Name = "Some other tier";

    var oldRules = new PriorityRules { Spread = 0.8, UseRoleOrder = false, Basis = NeedBasis.DpsGain };

    var folded = StaticProfile.FromLegacy(new LegacyConfigV1(
        Roster: oldRoster,
        ActiveTierId: "some-other-tier",
        Tier: oldTier,
        Kills: new Dictionary<int, int> { [1] = 7, [4] = 2 },
        Rules: oldRules,
        LookaheadWeeks: 14,
        ShowOnlyNextRecipient: true,
        Mode: AssignmentMode.Automatic,
        AnnounceInPartyChat: true,
        Mount: MountHandling.GreedOnly,
        AltCharacters: true,
        AltsPreferredForWeaponTokens: false,
        ActionDelayMs: 900,
        VerboseChat: true,
        ExpertMode: true,
        AutoReadGearOnEnter: false));

    Check("the roster moves across whole", folded.Roster.Count == 2 && folded.Roster[0].Name == "Astra");
    Check("with everything on the rows", folded.Roster[0].Tokens[3] == 5 && folded.Roster[1].IsAlt);
    Check("the tier comes with it", folded.Tier?.Name == "Some other tier");
    Check("and which tier it was", folded.ActiveTierId == "some-other-tier");
    Check("the kill counts survive", folded.KillsFor(1) == 7 && folded.KillsFor(4) == 2);

    var settings = folded.Settings;

    Check("the rules object is the same one, not a copy of the defaults",
          ReferenceEquals(settings.Rules, oldRules));

    // Every setting, one by one. This is the list that a new field has to be added to, and the
    // reason it is spelled out rather than looped: a loop would pass while missing the field it
    // was never told about.
    Check("lookahead", settings.LookaheadWeeks == 14);
    Check("assignment mode", settings.Mode == AssignmentMode.Automatic);
    Check("party chat", settings.AnnounceInPartyChat);
    Check("mount handling", settings.Mount == MountHandling.GreedOnly);
    Check("alt characters", settings.AltCharacters);
    Check("alts for weapon stones", !settings.AltsPreferredForWeaponTokens);
    Check("action delay", settings.ActionDelayMs == 900);
    Check("verbose chat", settings.VerboseChat);
    Check("expert mode", settings.ExpertMode);
    Check("auto gear scan", !settings.AutoReadGearOnEnter);
}

{
    // An install from before a setting existed says nothing about it, and nothing is not false.
    // Getting this wrong turns "the old config did not mention auto gear scan" into "switch it off".
    var sparse = StaticProfile.FromLegacy(new LegacyConfigV1(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));

    var fresh = new StaticProfile();

    Check("a missing setting keeps its default, not its zero",
          sparse.Settings.AutoReadGearOnEnter == fresh.Settings.AutoReadGearOnEnter &&
          sparse.Settings.LookaheadWeeks == fresh.Settings.LookaheadWeeks &&
          sparse.Settings.ActionDelayMs == fresh.Settings.ActionDelayMs &&
          sparse.Settings.Mode == fresh.Settings.Mode &&
          sparse.Settings.AltsPreferredForWeaponTokens == fresh.Settings.AltsPreferredForWeaponTokens);

    Check("and an empty config comes out as an empty static, not a broken one",
          sparse.Roster.Count == 0 && sparse.Kills.Count == 0 &&
          sparse.ActiveTierId == fresh.ActiveTierId);

    Check("two statics never share an id", new StaticProfile().Id != new StaticProfile().Id);
}

// --- decisions made by hand ------------------------------------------------------------------------

{
    RosterMember Wanting(string name) => Member(name,
                                                (GearSlot.Body, GearSource.Raid),
                                                (GearSlot.Legs, GearSource.Raid));

    List<PlayerPlan> Two() =>
    [
        Plan(Wanting("First"), RaidRole.Dps, tier, 0),
        Plan(Wanting("Second"), RaidRole.Dps, tier, 1),
    ];

    SimulationResult Run(ManualPlan? pins, List<PlayerPlan> plans) =>
        new WeekSimulator(tier, rules, 6, pins).Run(plans);

    // The assertion worth more than the rest of this block: a feature nobody switched on changes
    // nothing at all. Off, and the awards are the same awards, week for week and name for name.
    var plain = Run(null, Two());
    var offButPinned = new ManualPlan { Enabled = false };

    offButPinned.Pin(new ManualAward
    {
        Week = 1, Encounter = 3, Slot = GearSlot.Body, PlayerKey = "Second@Test",
    });

    var ignored = Run(offButPinned, Two());

    Check("switched off, a pin changes nothing",
          string.Join("|", plain.Awards.Select(a => $"{a.Week}:{a.What}:{a.PlayerName}")) ==
          string.Join("|", ignored.Awards.Select(a => $"{a.Week}:{a.What}:{a.PlayerName}")));

    Check("and reports no trouble", ignored.Trouble.Count == 0);

    // On, the pin beats the ranking. "First" is ahead on player order, so without the pin the body
    // coffer is theirs; the check is only meaningful because that is who wins by default.
    var byRules = Run(null, Two());
    var week1Body = byRules.Awards.First(a => a is { Week: 1, Bought: false } && a.What == "Body");

    Check("by the rules the first player takes the body", week1Body.PlayerName == "First");

    var pins = new ManualPlan { Enabled = true };
    pins.Pin(new ManualAward { Week = 1, Encounter = 3, Slot = GearSlot.Body, PlayerKey = "Second@Test" });

    var pinned = Run(pins, Two());
    var pinnedBody = pinned.Awards.First(a => a is { Week: 1, Bought: false } && a.What == "Body");

    Check("a pin overrules it", pinnedBody.PlayerName == "Second", pinnedBody.PlayerName);
    Check("and nothing is reported as wrong", pinned.Trouble.Count == 0,
          string.Join("; ", pinned.Trouble.Select(t => t.Message)));

    // The rest of the week still works itself out around the pin rather than freezing with it.
    var legs = pinned.Awards.FirstOrDefault(a => a is { Week: 1, Bought: false } && a.What == "Legs");
    Check("the rest of the week is still worked out", legs.PlayerName == "First", legs.PlayerName);
}

{
    // A pin nobody can honour is reported and not carried out. Both halves matter: a plan that
    // silently does something else is worse than one with a red line in it.
    var member = Member("Only", (GearSlot.Body, GearSource.Raid));

    var pins = new ManualPlan { Enabled = true };
    pins.Pin(new ManualAward { Week = 1, Encounter = 3, Slot = GearSlot.Legs, PlayerKey = "Only@Test" });

    var run = new WeekSimulator(tier, rules, 4, pins).Run([Plan(member, RaidRole.Dps, tier)]);

    Check("a pin on somebody who does not need it is reported",
          run.Trouble.Any(t => t.Message.Contains("does not need it")),
          string.Join("; ", run.Trouble.Select(t => t.Message)));

    Check("and is not carried out",
          run.Awards.All(a => a.What != "Legs"),
          string.Join(", ", run.Awards.Select(a => a.What)));

    // A player who has left takes the pin with them, and says so.
    var gone = new ManualPlan { Enabled = true };
    gone.Pin(new ManualAward { Week = 1, Encounter = 3, Slot = GearSlot.Body, PlayerKey = "Ghost@Test" });

    var orphaned = new WeekSimulator(tier, rules, 4, gone).Run([Plan(member, RaidRole.Dps, tier)]);

    Check("a pin on somebody who left is reported",
          orphaned.Trouble.Any(t => t.Message.Contains("no longer in this static")));

    // Nobody, deliberately: the coffer goes to no one and that is not a problem.
    var greed = new ManualPlan { Enabled = true };
    greed.Pin(new ManualAward { Week = 1, Encounter = 3, Slot = GearSlot.Body, PlayerKey = "" });

    var passed = new WeekSimulator(tier, rules, 4, greed).Run([Plan(member, RaidRole.Dps, tier)]);

    Check("a pin on nobody hands it to nobody",
          passed.Awards.All(a => !(a.Week == 1 && a.What == "Body" && !a.Bought)));

    Check("and is not a problem", passed.Trouble.Count == 0);
}

{
    // A purchase pinned earlier than the tomestones allow. The message counts in weeks, because
    // "three weeks early" is something somebody can act on and "410 short" is arithmetic homework.
    var member = Member("Saver", (GearSlot.Body, GearSource.Tome), (GearSlot.Legs, GearSource.Tome));

    var pins = new ManualPlan { Enabled = true };

    // 450 banked plus week one's 450 buys one 825 piece and not two.
    pins.Awards.Add(new ManualAward
    {
        Week = 1, Bought = true, WithTomestones = true, Slot = GearSlot.Body, PlayerKey = "Saver@Test",
    });

    pins.Awards.Add(new ManualAward
    {
        Week = 1, Bought = true, WithTomestones = true, Slot = GearSlot.Legs, PlayerKey = "Saver@Test",
    });

    var plan = Plan(member, RaidRole.Dps, tier);
    var run = new WeekSimulator(tier, rules, 6, pins).Run([plan]);

    Check("a purchase pinned too early is reported",
          run.Trouble.Any(t => t.Message.Contains("cannot afford")),
          string.Join("; ", run.Trouble.Select(t => t.Message)));

    // A separate player, owing one piece: pinned for week two, and week one must leave it alone.
    // That is the half the greedy pass used to break — it could afford the piece in week one and
    // bought it there, which does not fail the pin so much as quietly make it pointless.
    var waiter = Member("Waiter", (GearSlot.Body, GearSource.Tome));

    var later = new ManualPlan { Enabled = true };

    later.Awards.Add(new ManualAward
    {
        Week = 2, Bought = true, WithTomestones = true, Slot = GearSlot.Body, PlayerKey = "Waiter@Test",
    });

    var ok = new WeekSimulator(tier, rules, 6, later).Run([Plan(waiter, RaidRole.Dps, tier)]);

    Check("a purchase pinned for a later week is not bought earlier",
          ok.Awards.All(a => !(a.Bought && a.Week == 1)),
          string.Join(", ", ok.Awards.Where(a => a.Bought).Select(a => $"W{a.Week}:{a.What}:{a.TomeCost}")));

    Check("and happens in the week it was written down for",
          ok.Awards.Any(a => a is { Week: 2, Bought: true } && a.TomeCost == 825),
          string.Join(", ", ok.Awards.Where(a => a.Bought).Select(a => $"W{a.Week}:{a.What}:{a.TomeCost}")));

    Check("with nothing reported", ok.Trouble.Count == 0,
          string.Join("; ", ok.Trouble.Select(t => t.Message)));
}

{
    // Pins are one per drop. Changing your mind twice must leave one decision, not a pile in which
    // the oldest quietly wins.
    var pins = new ManualPlan { Enabled = true };

    pins.Pin(new ManualAward { Week = 1, Encounter = 3, Slot = GearSlot.Body, PlayerKey = "A@Test" });
    pins.Pin(new ManualAward { Week = 1, Encounter = 3, Slot = GearSlot.Body, PlayerKey = "B@Test" });

    Check("pinning the same drop twice keeps one row", pins.Awards.Count == 1);
    Check("and it is the later decision", pins.Awards[0].PlayerKey == "B@Test");

    pins.Unpin(1, 3, GearSlot.Body, null, 0);
    Check("unpinning removes it", pins.Awards.Count == 0);

    // A shorter horizon leaves pins behind that can never run.
    pins.Pin(new ManualAward { Week = 9, Encounter = 1, Slot = GearSlot.Earrings, PlayerKey = "A@Test" });
    Check("pins past the horizon can be dropped", pins.DropBeyond(6) == 1 && pins.Awards.Count == 0);
}

// --- the calendar ----------------------------------------------------------------------------------

{
    // Every one of these is a moment somebody's code is wrong at exactly once a week, which is why
    // they are written down rather than reasoned about. Tuesday 08:00 UTC is the reset.
    const DayOfWeek reset = DayOfWeek.Tuesday;
    const int eight = 8 * 60;

    var tuesdayMorning = new DateTime(2026, 8, 18, 7, 0, 0, DateTimeKind.Utc);   // Tuesday, before it
    var tuesdayNoon = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);     // Tuesday, after it
    var friday = new DateTime(2026, 8, 21, 21, 0, 0, DateTimeKind.Utc);

    // The case that catches a naive "walk back to Tuesday": on reset day before the hour, the last
    // reset is a week ago, not this morning.
    Check("before the hour on reset day, the last reset was a week back",
          RaidCalendar.LastReset(reset, eight, tuesdayMorning) ==
          new DateTime(2026, 8, 11, 8, 0, 0, DateTimeKind.Utc),
          RaidCalendar.LastReset(reset, eight, tuesdayMorning).ToString("ddd dd MMM HH:mm"));

    Check("after the hour it is today",
          RaidCalendar.LastReset(reset, eight, tuesdayNoon) ==
          new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc));

    Check("and mid-week it is the Tuesday behind you",
          RaidCalendar.LastReset(reset, eight, friday) ==
          new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc));

    Check("the next reset is always a week after the last",
          RaidCalendar.NextReset(reset, eight, friday) ==
          RaidCalendar.LastReset(reset, eight, friday).AddDays(7));

    // Week 1 is the lockout that is running, so a Friday sits inside it.
    var (start, end) = RaidCalendar.WeekWindow(reset, eight, 1, friday);

    Check("week one is the lockout you are in", start <= friday && friday < end,
          $"{start:dd MMM} – {end:dd MMM}");

    Check("and week three starts a fortnight later",
          RaidCalendar.WeekWindow(reset, eight, 3, friday).StartUtc == start.AddDays(14));

    Check("a moment inside this lockout is week one",
          RaidCalendar.WeekOf(reset, eight, friday, friday) == 1);

    Check("and one nine days out is week two",
          RaidCalendar.WeekOf(reset, eight, friday.AddDays(9), friday) == 2);
}

{
    // Sessions. Thursday 19:00 UTC for three hours.
    var slot = new RaidSlot { Day = DayOfWeek.Thursday, StartMinutesUtc = 19 * 60, DurationMinutes = 180 };

    var wednesday = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
    var duringIt = new DateTime(2026, 8, 20, 20, 0, 0, DateTimeKind.Utc);
    var afterIt = new DateTime(2026, 8, 20, 23, 0, 0, DateTimeKind.Utc);

    Check("the next session is tomorrow evening",
          RaidCalendar.NextOccurrence(slot, wednesday) ==
          new DateTime(2026, 8, 20, 19, 0, 0, DateTimeKind.Utc));

    // A session under way is "now", not "in a week" -- which is what lets the bar say raiding
    // rather than counting down to the next one while you are in it.
    Check("one under way still counts as now",
          RaidCalendar.NextOccurrence(slot, duringIt) ==
          new DateTime(2026, 8, 20, 19, 0, 0, DateTimeKind.Utc));

    Check("and once it has ended, next week",
          RaidCalendar.NextOccurrence(slot, afterIt) ==
          new DateTime(2026, 8, 27, 19, 0, 0, DateTimeKind.Utc));

    List<RaidSlot> two =
    [
        slot,
        new RaidSlot { Day = DayOfWeek.Tuesday, StartMinutesUtc = 19 * 60, DurationMinutes = 180 },
    ];

    Check("the soonest of several is the next one",
          RaidCalendar.Next(two, wednesday)!.Value.StartUtc ==
          new DateTime(2026, 8, 20, 19, 0, 0, DateTimeKind.Utc));

    Check("nothing scheduled is no next session", RaidCalendar.Next([], wednesday) == null);

    Check("running is true inside a session",
          RaidCalendar.Running(two, duringIt, out var endsAt) &&
          endsAt == new DateTime(2026, 8, 20, 22, 0, 0, DateTimeKind.Utc));

    Check("and false outside one", !RaidCalendar.Running(two, wednesday, out _));
}

{
    Check("minutes past midnight read as a clock", RaidCalendar.Clock(19 * 60 + 30) == "19:30");
    Check("and midnight is not 24:00", RaidCalendar.Clock(0) == "00:00");

    // Out of range folds rather than throwing: a spinner that runs past midnight should wrap, and
    // an hour before midnight is 23:00 rather than an exception.
    Check("a day's worth wraps to the same time", RaidCalendar.Clock(24 * 60) == "00:00");
    Check("and going backwards wraps too", RaidCalendar.Clock(-60) == "23:00");

    // The round trip is the property that matters: whatever the machine's timezone, writing a local
    // moment down in UTC and reading it back has to give the same local moment.
    var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    foreach (var day in Enum.GetValues<DayOfWeek>())
    {
        foreach (var minutes in new[] { 0, 7 * 60, 19 * 60 + 30, 23 * 60 + 59 })
        {
            var (utcDay, utcMinutes) = RaidCalendar.ToUtc(day, minutes, now);
            var (backDay, backMinutes) = RaidCalendar.ToLocal(utcDay, utcMinutes, now);

            if (backDay == day && backMinutes == minutes)
                continue;

            Check($"local {day} {RaidCalendar.Clock(minutes)} survives a trip through UTC", false,
                  $"came back as {backDay} {RaidCalendar.Clock(backMinutes)}");
            break;
        }
    }

    Check("every local time survives a trip through UTC", true);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;

static class HarnessExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
