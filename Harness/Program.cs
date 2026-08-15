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

    // Book costs, as the SpecialShop scan would have produced them.
    void Buy(int encounter, int cost, GearSlot? slot, GearSide? upgrade) =>
        tier.Rewards.Add(new TierReward
        {
            Encounter = encounter, Cost = cost, ItemId = (uint)(100 + tier.Rewards.Count),
            Slot = slot, Upgrade = upgrade,
        });

    Buy(1, 4, GearSlot.Earrings, null);
    Buy(1, 4, GearSlot.Necklace, null);
    Buy(1, 4, GearSlot.Bracelets, null);
    Buy(1, 4, GearSlot.Ring1, null);
    Buy(2, 4, GearSlot.Head, null);
    Buy(2, 4, GearSlot.Hands, null);
    Buy(2, 4, GearSlot.Feet, null);
    Buy(2, 4, null, GearSide.Right);
    Buy(3, 6, GearSlot.Body, null);
    Buy(3, 6, GearSlot.Legs, null);
    Buy(3, 4, null, GearSide.Left);
    Buy(4, 8, GearSlot.Weapon, null);
    Buy(4, 5, GearSlot.OffHand, null);
    Buy(4, 4, null, GearSide.Weapon);

    return tier;
}

static RosterMember Member(string name, params (GearSlot Slot, GearSource Source)[] needs)
{
    var member = new RosterMember { Name = name, World = "Test" };
    foreach (var (slot, src) in needs)
        member.NeedFor(slot).Source = src;

    return member;
}

static PlayerPlan Plan(RosterMember member, RaidRole role, TierDefinition tier) =>
    PlayerPlan.From(member, role, tier);

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

// --- the slot is read out of a coffer's name ------------------------------------------------------

{
    var accessories = new[] { GearSlot.Earrings, GearSlot.Necklace, GearSlot.Bracelets, GearSlot.Ring1 };
    var armour = new[] { GearSlot.Head, GearSlot.Hands, GearSlot.Feet };
    var weapons = new[] { GearSlot.Weapon, GearSlot.OffHand };

    Check("head coffer reads as head",
          Slots.SlotFromName("Grand Champion's Head Gear Coffer (IL 790)", armour) == GearSlot.Head);

    Check("foot coffer reads as feet",
          Slots.SlotFromName("Grand Champion's Foot Gear Coffer (IL 790)", armour) == GearSlot.Feet);

    // "Earring" contains "ring": the ordering inside SlotWords is what stops this landing on Ring1.
    Check("earring coffer does not read as a ring",
          Slots.SlotFromName("Grand Champion's Earring Coffer (IL 790)", accessories) == GearSlot.Earrings);

    Check("ring coffer reads as a ring",
          Slots.SlotFromName("Grand Champion's Ring Coffer (IL 790)", accessories) == GearSlot.Ring1);

    Check("shield coffer reads as the off hand",
          Slots.SlotFromName("Grand Champion's Shield Coffer (IL 790)", weapons) == GearSlot.OffHand);

    // A read is only ever allowed to land on a slot the same book already buys.
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

// --- the simulator prefers whoever is furthest from done -----------------------------------------

{
    var nearlyDone = Member("Near", (GearSlot.Body, GearSource.Raid));
    var farOff = Member("Far",
                        (GearSlot.Body, GearSource.Raid),
                        (GearSlot.Legs, GearSource.Raid),
                        (GearSlot.Head, GearSource.Raid),
                        (GearSlot.Hands, GearSource.Raid));

    var plans = new List<PlayerPlan> { Plan(nearlyDone, RaidRole.Dps, tier), Plan(farOff, RaidRole.Tank, tier) };
    var result = new WeekSimulator(tier, rules, 12).Run(plans);

    var first = result.Awards.First(a => a.Slot == GearSlot.Body);
    Check("the first body coffer goes to the player with more left", first.PlayerName == "Far",
          $"went to {first.PlayerName}");
}

// --- damage dealers win a tie --------------------------------------------------------------------

{
    var dps = Member("Dps", (GearSlot.Body, GearSource.Raid));
    var tank = Member("Tank", (GearSlot.Body, GearSource.Raid));

    var plans = new List<PlayerPlan> { Plan(tank, RaidRole.Tank, tier), Plan(dps, RaidRole.Dps, tier) };
    var result = new WeekSimulator(tier, rules, 12).Run(plans);

    var first = result.Awards.First(a => a.Slot == GearSlot.Body);
    Check("an otherwise even tie goes to the damage dealer", first.PlayerName == "Dps",
          $"went to {first.PlayerName}");
}

// --- handing a needed piece out never scores worse than holding it -------------------------------

{
    var slow = Member("Slow",
                      (GearSlot.Body, GearSource.Raid), (GearSlot.Legs, GearSource.Raid),
                      (GearSlot.Head, GearSource.Raid), (GearSlot.Hands, GearSource.Raid),
                      (GearSlot.Feet, GearSource.Raid));

    var quick = Member("Quick", (GearSlot.Body, GearSource.Raid));

    var simulator = new WeekSimulator(tier, rules, 12);

    List<PlayerPlan> Fresh() => [Plan(slow, RaidRole.Dps, tier), Plan(quick, RaidRole.Dps, tier)];

    var baseline = simulator.Score(simulator.Run(Fresh()), 2);

    var toSlow = Fresh();
    toSlow[0].TakeSlot(GearSlot.Body);
    var slowScore = simulator.Score(simulator.Run(toSlow), 2);

    var toQuick = Fresh();
    toQuick[1].TakeSlot(GearSlot.Body);
    var quickScore = simulator.Score(simulator.Run(toQuick), 2);

    Check("giving the piece to someone beats nobody taking it",
          slowScore <= baseline && quickScore <= baseline,
          $"baseline {baseline:0.00}, slow {slowScore:0.00}, quick {quickScore:0.00}");

    // Both leave the group finishing in the same week, so the tiebreak is the weighted average —
    // and finishing someone outright this week beats shortening a queue nobody is waiting on.
    var toSlowRun = simulator.Run(Fresh().Also(p => p[0].TakeSlot(GearSlot.Body)));
    var toQuickRun = simulator.Run(Fresh().Also(p => p[1].TakeSlot(GearSlot.Body)));

    Check("neither choice changes the group's last week here",
          toSlowRun.LastFinishWeek == toQuickRun.LastFinishWeek,
          $"{toSlowRun.LastFinishWeek} vs {toQuickRun.LastFinishWeek}");

    Check("with the last week tied, the piece finishes the player it completes",
          quickScore < slowScore, $"quick {quickScore:0.00} < slow {slowScore:0.00}");
}

// --- a shield is bought, never dropped -------------------------------------------------------------

{
    // No fight drops a shield; it only exists in the weapon book exchange. It must still be planned.
    var m = Member("Pld", (GearSlot.OffHand, GearSource.Raid));
    var plan = Plan(m, RaidRole.Tank, tier);

    Check("a shield still becomes a need", plan.Open.Count == 1,
          string.Join(", ", plan.Open.Select(n => n.Describe())));

    Check("the shield is paid for with weapon books",
          plan.Open.Count == 1 && plan.Open[0].Encounter == 4, $"encounter {plan.Open.FirstOrDefault().Encounter}");

    // Five books at one a week.
    var result = new WeekSimulator(tier, rules, 12).Run([Plan(m, RaidRole.Tank, tier)]);
    Check("the shield arrives on book five", result.LastFinishWeek == 5, $"week {result.LastFinishWeek}");
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
