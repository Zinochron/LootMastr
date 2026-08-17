using LootMastr.Data;
using LootMastr.Planning;
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
    // Same need, but the coffer is taken away: eight books at one a week means week 8.
    var noDrops = Tier();
    noDrops.Encounter(4)!.DropCount = 0;

    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    var result = new WeekSimulator(noDrops, rules, 12).Run([Plan(m, RaidRole.Dps, noDrops)]);
    Check("with no drops the weapon is bought on book eight", result.LastFinishWeek == 8,
          $"week {result.LastFinishWeek}");
}

{
    // Starting with books already in hand pulls that in.
    var noDrops = Tier();
    noDrops.Encounter(4)!.DropCount = 0;

    var m = Member("A", (GearSlot.Weapon, GearSource.Raid));
    m.Tokens[4] = 6;

    var result = new WeekSimulator(noDrops, rules, 12).Run([Plan(m, RaidRole.Dps, noDrops)]);
    Check("books already held count", result.LastFinishWeek == 2, $"week {result.LastFinishWeek}");
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
