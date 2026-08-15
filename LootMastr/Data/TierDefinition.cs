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
    /// How an augmented tomestone piece is spelled. Augmented gear sits at exactly the raid item
    /// level, so without this a gear set imported before <see cref="Augments"/> has been discovered
    /// would read every augmented piece as a raid drop. Editable because the wording is localised.
    /// </summary>
    public List<string> AugmentedNamePrefixes { get; set; } = ["Augmented ", "Aug. "];

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

        var reward = RewardForSlot(slot);
        return reward is { Cost: > 0 } ? new BookCost(reward.Encounter, reward.Cost) : null;
    }

    public BookCost? CostForUpgrade(GearSide side)
    {
        var rule = CostRules.Where(r => r.Upgrade == side).MinBy(r => r.Cost);
        if (rule != null)
            return new BookCost(rule.Encounter, rule.Cost);

        var reward = RewardForUpgrade(side);
        return reward is { Cost: > 0 } ? new BookCost(reward.Encounter, reward.Cost) : null;
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
        foreach (var reward in Rewards.Where(r => r.IsAssigned && r.Cost > 0))
        {
            var cost = reward.Slot != null ? CostForSlot(reward.Slot.Value) : CostForUpgrade(reward.Upgrade!.Value);
            if (cost == null || (cost.Encounter == reward.Encounter && cost.Cost == reward.Cost))
                continue;

            var what = reward.Slot?.Label() ?? $"{reward.Upgrade} upgrade";
            yield return $"{what}: rule says {cost.Cost} × {Encounter(cost.Encounter)?.Name ?? "?"}, " +
                         $"shop says {reward.Cost} × {Encounter(reward.Encounter)?.Name ?? "?"}.";
        }
    }

    /// <summary>Problems worth showing in the tier tab. Empty means everything resolved.</summary>
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

    /// <summary>Slots this rule prices. Rings are filed under <c>Ring1</c>.</summary>
    public List<GearSlot> Slots { get; set; } = new();

    /// <summary>Set instead of <see cref="Slots"/> when the rule prices an upgrade material.</summary>
    public GearSide? Upgrade { get; set; }
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
