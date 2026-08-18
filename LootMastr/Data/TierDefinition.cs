using System;
using System.Collections.Generic;
using System.Linq;

namespace LootMastr.Data;

/// <summary>
/// One savage tier: which fight drops what, which book it hands out, and what those books buy.
/// Shipped as json under <c>Data/Tiers</c> and copied into the config on first use, so edits made
/// in game survive without touching the installed files.
/// </summary>
[Serializable]
public sealed class TierDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public ushort RaidItemLevel { get; set; }
    public ushort RaidWeaponItemLevel { get; set; }
    public ushort TomeItemLevel { get; set; }
    public ushort AugmentedItemLevel { get; set; }

    public List<TierEncounter> Encounters { get; set; } = new();
    public List<TierUpgrade> Upgrades { get; set; } = new();

    /// <summary>
    /// The stone the tomestone weapon needs, on top of its tomestone price. Null when the tier has
    /// none, which is how every tier reads until somebody points this at an item.
    /// </summary>
    public TierSpecialDrop? WeaponToken { get; set; }

    /// <summary>The mount the last fight always drops. Null until somebody names it.</summary>
    public TierSpecialDrop? Mount { get; set; }

    /// <summary>
    /// How an augmented tomestone piece is spelled. Augmented gear sits at exactly the raid item
    /// level, so without this a gear set imported before <see cref="Augments"/> has been discovered
    /// would read every augmented piece as a raid drop. Editable because the wording is localised.
    /// </summary>
    public List<string> AugmentedNamePrefixes { get; set; } = ["Augmented ", "Aug. "];

    /// <summary>
    /// Whether every fight always drops one of each slot it can — the first fight putting up all
    /// four accessories, the last putting up its one weapon.
    ///
    /// With it off, a fight drops <see cref="TierEncounter.DropCount"/> coffers out of its pool and
    /// the same one can come up more than once. The difference is not cosmetic: four guaranteed
    /// accessories a week and two random ones out of four are different tiers to gear through.
    ///
    /// On by default, because that is how savage tiers have worked for years now. It only affects
    /// the weeks the projection has to guess at — the coming week always lists every coffer a fight
    /// can put up, since "what could turn up on Friday" is not a question about drop rates.
    /// </summary>
    public bool AllCoffersDrop { get; set; } = true;

    /// <summary>
    /// Tomestones a character can collect in one week. The game's own cap, and everyone hits it.
    ///
    /// A number rather than a constant because it is the sort of thing an expansion changes, and
    /// because a group that knows it will miss a week can say so by lowering it.
    /// </summary>
    public int TomestonesPerWeek { get; set; } = 450;

    /// <summary>
    /// Weeks of tomestones already in the bank when the tier opens.
    ///
    /// Savage releases a week after the patch, so everyone starts with at least one week banked —
    /// and a group that starts the tier late starts with several. This is the single number that
    /// decides whether the first body piece is affordable in week one or week three, which is why
    /// it is asked for rather than assumed.
    /// </summary>
    public int PriorTomeWeeks { get; set; } = 1;

    /// <summary>
    /// What the shop actually sells, discovered from <c>SpecialShop</c>. Used to show the exchange
    /// and as a fallback for costs; <see cref="CostRules"/> is what the arithmetic runs on.
    /// </summary>
    public List<TierReward> Rewards { get; set; } = new();

    /// <summary>
    /// What each kind of piece costs in books. Costs are uniform per category rather than per item,
    /// so six rules say everything a per-item table would — and unlike the discovered table, a
    /// rule cannot be half missing.
    /// </summary>
    public List<TierCostRule> CostRules { get; set; } = new();

    /// <summary>
    /// Books that can be traded for other books. The last fight's books convert into any earlier
    /// fight's, which is why a player sitting on spare weapon books is not as stuck as their counts
    /// make them look.
    /// </summary>
    public List<TierTokenConversion> Conversions { get; set; } = new();

    /// <summary>
    /// The augmented tomestone set, also discovered rather than typed: augmented pieces sit at the
    /// same item level as raid gear, so nothing but the upgrade trade itself tells them apart.
    /// </summary>
    public List<TierAugment> Augments { get; set; } = new();

    public TierEncounter? Encounter(int index) => Encounters.FirstOrDefault(e => e.Index == index);

    /// <summary>
    /// Where a piece of gear comes from, from nothing but its item level and its name — no shop
    /// data involved. Null when the levels do not account for it, which is the caller's cue to
    /// fall back on the discovered trade.
    ///
    /// The whole rule in two lines: the item level says whether the piece belongs to this tier,
    /// and the name says which half of it, because augmented tomestone gear is spelled
    /// <c>Augmented …</c> and raid gear never is.
    /// </summary>
    public GearSource? ClassifyByLevel(ushort itemLevel, string name)
    {
        var augmented = IsAugmentedName(name);

        if (IsTierItemLevel(itemLevel))
            return augmented ? GearSource.TomeAugmented : GearSource.Raid;

        if (itemLevel != 0 && itemLevel == TomeItemLevel)
            return augmented ? GearSource.TomeAugmented : GearSource.Tome;

        return null;
    }

    /// <summary>
    /// Item levels this tier's endgame gear sits at. Raid gear and augmented tomestone gear share
    /// one, which is exactly why the name is needed to tell them apart.
    /// </summary>
    public bool IsTierItemLevel(ushort itemLevel) =>
        itemLevel != 0 &&
        (itemLevel == RaidItemLevel || itemLevel == RaidWeaponItemLevel || itemLevel == AugmentedItemLevel);

    /// <summary>
    /// Whether an item's name marks it as augmented tomestone gear. Augmented pieces are spelled
    /// <c>Augmented …</c>, raid pieces never are, and the two sit at the same item level — so this
    /// is the whole of the distinction.
    /// </summary>
    public bool IsAugmentedName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var prefix in AugmentedNamePrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix) &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Which fight drops the coffer for this slot.</summary>
    public TierEncounter? EncounterForSlot(GearSlot slot)
    {
        var coffer = Slots.CofferSlot(slot);
        return Encounters.FirstOrDefault(e => e.DropSlots.Contains(coffer));
    }

    /// <summary>Which fight drops the upgrade material for this side.</summary>
    public TierEncounter? EncounterForUpgrade(GearSide side) =>
        Encounters.FirstOrDefault(e => e.UpgradeDrops.Contains(side));

    public TierUpgrade? UpgradeFor(GearSide side) => Upgrades.FirstOrDefault(u => u.Side == side);

    /// <summary>Cheapest way to buy this slot's piece with books, or null if nothing was discovered.</summary>
    public TierReward? RewardForSlot(GearSlot slot)
    {
        var coffer = Slots.CofferSlot(slot);
        return Rewards.Where(r => r.Slot == coffer).MinBy(r => r.Cost);
    }

    public TierReward? RewardForUpgrade(GearSide side) =>
        Rewards.Where(r => r.Upgrade == side).MinBy(r => r.Cost);

    /// <summary>
    /// What this slot costs in books. The rules answer first because they are complete by
    /// construction; the discovered table only fills gaps.
    /// </summary>
    public BookCost? CostForSlot(GearSlot slot)
    {
        var coffer = Slots.CofferSlot(slot);

        var rule = CostRules.Where(r => r.Slots.Contains(coffer)).MinBy(r => r.Cost);
        if (rule != null)
            return new BookCost(rule.Encounter, rule.Cost);

        return ShopCostFor(Rewards.Where(r => r.Slot == coffer));
    }

    /// <summary>
    /// What the tomestone version of a slot costs, or null when the tier does not price one.
    ///
    /// Only the rules, never the discovered shop table: the book exchange is what
    /// <see cref="Rewards"/> holds, and the tomestone vendor is a different NPC that this plugin
    /// does not read. These numbers are typed in, and a missing one has to read as "unknown"
    /// rather than as "free".
    /// </summary>
    public int? TomeCostForSlot(GearSlot slot)
    {
        var coffer = Slots.CofferSlot(slot);

        var rule = CostRules.Where(r => r.TomeCost > 0 && r.Slots.Contains(coffer)).MinBy(r => r.TomeCost);
        return rule?.TomeCost;
    }

    /// <summary>
    /// Fills in the standard tomestone prices, and only where nothing is set.
    ///
    /// Same rule as <c>DeriveCostRules</c>: a number somebody typed in is a decision and is never
    /// overwritten. The prices themselves have been the same for several expansions — 825 for the
    /// two big armour pieces, 495 for the three small ones, 375 for an accessory, 500 for a weapon
    /// that also wants a stone — so seeding them saves four fields of typing and stays editable.
    /// </summary>
    public bool SeedTomeCosts()
    {
        var changed = false;

        foreach (var rule in CostRules)
        {
            if (rule.TomeCost > 0 || rule.Upgrade != null || rule.Slots.Count == 0)
                continue;

            var price = StandardTomePrice(rule.Slots);
            if (price == 0)
                continue;

            rule.TomeCost = price;
            changed = true;
        }

        return changed;
    }

    /// <summary>The going rate for whichever category a rule's slots fall into. 0 when they are mixed.</summary>
    private static int StandardTomePrice(IEnumerable<GearSlot> slots)
    {
        var price = 0;

        foreach (var slot in slots)
        {
            var own = Slots.CofferSlot(slot) switch
            {
                GearSlot.Body or GearSlot.Legs => 825,
                GearSlot.Head or GearSlot.Hands or GearSlot.Feet => 495,
                GearSlot.Earrings or GearSlot.Necklace or GearSlot.Bracelets or GearSlot.Ring1 => 375,
                GearSlot.Weapon => 500,
                _ => 0,
            };

            if (own == 0)
                return 0;

            // A rule that mixes categories is priced by nothing, which is the honest answer: it
            // would have to be split before either number could be right.
            if (price != 0 && price != own)
                return 0;

            price = own;
        }

        return price;
    }

    public BookCost? CostForUpgrade(GearSide side)
    {
        var rule = CostRules.Where(r => r.Upgrade == side).MinBy(r => r.Cost);
        if (rule != null)
            return new BookCost(rule.Encounter, rule.Cost);

        return ShopCostFor(Rewards.Where(r => r.Upgrade == side));
    }

    /// <summary>
    /// What the shop charges for a category, taken as the most common price rather than the
    /// cheapest. A slot's gear is uniformly priced, so the odd one out is an outlier and not a
    /// bargain: a shield sold separately for three books sits under the weapon slot and would
    /// otherwise have priced every weapon in the tier at three.
    /// </summary>
    public static BookCost? ShopCostFor(IEnumerable<TierReward> rewards)
    {
        var priced = rewards.Where(r => r.Cost > 0).ToList();
        if (priced.Count == 0)
            return null;

        var group = priced.GroupBy(r => (r.Encounter, r.Cost))
                          .OrderByDescending(g => g.Count())
                          .ThenByDescending(g => g.Key.Cost)
                          .First();

        return new BookCost(group.Key.Encounter, group.Key.Cost);
    }

    /// <summary>
    /// Which hand-decided drop an item is, or null for anything the ranking handles.
    ///
    /// By item id, never by name. <see cref="TierCatalog.TryMatch"/> ends by reading a slot out of
    /// an item's name, so a mount with "ring" in its name would come through as an accessory coffer,
    /// and a stone recognised by name would break the day the tier is played on another language
    /// client.
    /// </summary>
    public SpecialDrop? SpecialFor(uint itemId)
    {
        if (itemId == 0)
            return null;

        if (WeaponToken is { ItemId: not 0 } token && itemId == token.ItemId)
            return SpecialDrop.WeaponToken;

        if (Mount is { ItemId: not 0 } mount && itemId == mount.ItemId)
            return SpecialDrop.Mount;

        // Only once the stone exists. Without one there is no tomestone weapon in play, and the
        // weapon material is an ordinary need the ranking answers perfectly well.
        if (WeaponToken is { ItemId: not 0 } &&
            UpgradeFor(GearSide.Weapon) is { ItemId: not 0 } material && itemId == material.ItemId)
        {
            return SpecialDrop.WeaponAugment;
        }

        return null;
    }

    /// <summary>Encounters whose books this encounter's books can be traded for.</summary>
    public IReadOnlyList<int> ConvertsInto(int encounter) =>
        Conversions.FirstOrDefault(c => c.FromEncounter == encounter)?.ToEncounters ?? [];

    /// <summary>Which encounter's books can be spent in place of this one's, if any.</summary>
    public int? ConvertibleSourceFor(int encounter)
    {
        foreach (var conversion in Conversions)
        {
            if (conversion.FromEncounter != encounter && conversion.ToEncounters.Contains(encounter))
                return conversion.FromEncounter;
        }

        return null;
    }

    /// <summary>
    /// Places where the shop disagrees with a cost rule. Not an error — the rules are what count —
    /// but a reliable sign that the tier changed and the rules have not caught up.
    /// </summary>
    public IEnumerable<string> CostMismatches()
    {
        // One line per category, against what the shop mostly charges for it. Comparing every
        // reward individually produced a wall of near-identical warnings — twenty weapons all
        // disagreeing with the same rule in the same way says nothing twenty times.
        foreach (var group in Rewards.Where(r => r.IsAssigned && r.Cost > 0)
                                     .GroupBy(r => (r.Slot, r.Upgrade)))
        {
            var shop = ShopCostFor(group);
            if (shop == null)
                continue;

            var rule = group.Key.Slot != null
                           ? CostForSlot(group.Key.Slot.Value)
                           : CostForUpgrade(group.Key.Upgrade!.Value);

            if (rule == null || (rule.Encounter == shop.Encounter && rule.Cost == shop.Cost))
                continue;

            var what = group.Key.Slot?.CofferLabel() ?? $"{group.Key.Upgrade} upgrade";
            yield return $"{what}: rule says {rule.Cost} × {Encounter(rule.Encounter)?.Name ?? "?"}, " +
                         $"shop mostly charges {shop.Cost} × {Encounter(shop.Encounter)?.Name ?? "?"}.";
        }
    }

    /// <summary>Problems worth showing in the tier window. Empty means everything resolved.</summary>
    public IEnumerable<string> Problems()
    {
        foreach (var encounter in Encounters.OrderBy(e => e.Index))
        {
            if (encounter.TokenItemId == 0)
                yield return $"{encounter.Name}: book \"{encounter.TokenItemName}\" does not match any item.";
        }

        foreach (var upgrade in Upgrades)
        {
            if (upgrade.ItemId == 0)
                yield return $"{upgrade.Side} upgrade: \"{upgrade.ItemName}\" does not match any item.";
        }

        // These matter more than the shop data does: gear is told apart by level and name, so a
        // level that is wrong or missing is what actually breaks the roster grid.
        if (RaidItemLevel == 0)
            yield return "Raid item level is not set — raid gear cannot be recognised without it.";

        if (TomeItemLevel == 0)
            yield return "Tomestone item level is not set — plain tome gear cannot be recognised.";

        if (Rewards.Count == 0)
            yield return "Book costs not discovered yet — press \"Discover exchange\" while logged in. " +
                         "Only the planner's \"can buy with books\" needs them; gear is classified " +
                         "from item level and name either way.";
    }
}

/// <summary>A number of books of one fight.</summary>
public sealed record BookCost(int Encounter, int Cost);

/// <summary>
/// What one category of piece costs. Categories rather than items, because that is how the game
/// actually prices them: every accessory costs the same, every one of head, hands and feet costs
/// the same, and so on.
/// </summary>
[Serializable]
public sealed class TierCostRule
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Which fight's books pay for it.</summary>
    public int Encounter { get; set; }

    public int Cost { get; set; }

    /// <summary>
    /// What the same category costs in tomestones, from the other shop. 0 when it has no tome
    /// version — a material is bought with books only.
    ///
    /// It sits on the same rule as the book price rather than in a table of its own because the two
    /// shops group the slots <b>identically</b>: accessories together, head/hands/feet together,
    /// body and legs together, the weapon on its own. A second table would be a second copy of that
    /// grouping, free to drift from this one.
    /// </summary>
    public int TomeCost { get; set; }

    /// <summary>Slots this rule prices. Rings are filed under <c>Ring1</c>.</summary>
    public List<GearSlot> Slots { get; set; } = new();

    /// <summary>Set instead of <see cref="Slots"/> when the rule prices an upgrade material.</summary>
    public GearSide? Upgrade { get; set; }
}

/// <summary>
/// A drop the need lists cannot describe, decided by hand.
///
/// Three of them, with nothing in common except that: a stone, the material that augments what the
/// stone buys, and a mount. None fills a gear slot, so none can be ranked — the plugin supplies an
/// order and a person presses the button.
/// </summary>
public enum SpecialDrop
{
    WeaponToken,
    WeaponAugment,
    Mount,
}

/// <summary>
/// Something a fight hands out that no need list can describe.
///
/// The stone the tomestone weapon wants and the mount from the last fight are both like this: they
/// come out of a chest, they matter to the group, and neither fills a gear slot. So neither can go
/// through the ranking, and both get decided by hand — with the plugin supplying an order rather
/// than an answer.
///
/// Matched strictly by <see cref="ItemId"/>. <c>TierCatalog.TryMatch</c> falls back to reading a
/// slot out of an item's name, and a mount or a stone whose name happens to contain "ring" would
/// otherwise be filed as a coffer.
/// </summary>
[Serializable]
public sealed class TierSpecialDrop
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Which fight drops it. Display only — the item id is what recognises it.</summary>
    public int Encounter { get; set; }
}

/// <summary>
/// Books of one fight being tradeable for books of another. Only ever generous in one direction:
/// the last fight's books buy the earlier ones, not the other way round.
/// </summary>
[Serializable]
public sealed class TierTokenConversion
{
    public int FromEncounter { get; set; }

    public List<int> ToEncounters { get; set; } = new();

    /// <summary>How many source books one target book costs. One, at least in every tier so far.</summary>
    public int Ratio { get; set; } = 1;
}

/// <summary>One augmented tomestone piece, and the plain piece plus material it is traded from.</summary>
[Serializable]
public sealed class TierAugment
{
    public uint AugmentedItemId { get; set; }
    public string AugmentedItemName { get; set; } = string.Empty;

    public uint BaseItemId { get; set; }
    public string BaseItemName { get; set; } = string.Empty;

    public GearSide Side { get; set; }
    public GearSlot? Slot { get; set; }
}

[Serializable]
public sealed class TierEncounter
{
    /// <summary>1..4, matching the order the fights are cleared in.</summary>
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public string TokenItemName { get; set; } = string.Empty;

    /// <summary>Resolved from <see cref="TokenItemName"/>; 0 means the name did not match.</summary>
    public uint TokenItemId { get; set; }

    /// <summary>Slots whose coffers can drop here. Rings are filed under <c>Ring1</c>.</summary>
    public List<GearSlot> DropSlots { get; set; } = new();

    /// <summary>Sides whose upgrade material drops here.</summary>
    public List<GearSide> UpgradeDrops { get; set; } = new();

    /// <summary>Gear coffers a full clear puts in the chest. Only used to look ahead.</summary>
    public int DropCount { get; set; } = 2;

    /// <summary>
    /// Zone this fight is in, learned the first time its chest is seen rather than shipped.
    /// 0 until then.
    /// </summary>
    public uint TerritoryId { get; set; }
}

[Serializable]
public sealed class TierUpgrade
{
    public GearSide Side { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
}

/// <summary>One line of the book exchange: this many books of that fight buys this item.</summary>
[Serializable]
public sealed class TierReward
{
    public int Encounter { get; set; }
    public int Cost { get; set; }
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Set when the reward is a gear coffer.</summary>
    public GearSlot? Slot { get; set; }

    /// <summary>Set when the reward is an upgrade material.</summary>
    public GearSide? Upgrade { get; set; }

    public bool IsAssigned => Slot != null || Upgrade != null;
}
