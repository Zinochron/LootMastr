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
    /// The book exchange, discovered from <c>SpecialShop</c> rather than typed in — costs are the
    /// kind of detail that is wrong in half the guides and changes between tiers.
    /// </summary>
    public List<TierReward> Rewards { get; set; } = new();

    /// <summary>
    /// The augmented tomestone set, also discovered rather than typed: augmented pieces sit at the
    /// same item level as raid gear, so nothing but the upgrade trade itself tells them apart.
    /// </summary>
    public List<TierAugment> Augments { get; set; } = new();

    public TierEncounter? Encounter(int index) => Encounters.FirstOrDefault(e => e.Index == index);

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

        if (Rewards.Count == 0)
            yield return "Book costs not discovered yet — press \"Discover exchange\" while logged in.";

        if (Augments.Count == 0)
            yield return "Augmented tome gear not discovered yet — imported gear sets cannot tell " +
                         "augmented pieces from raid pieces until it is.";
    }
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
