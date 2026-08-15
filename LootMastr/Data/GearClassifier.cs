using System;
using System.Collections.Generic;
using System.Linq;

namespace LootMastr.Data;

/// <summary>
/// Decides where a piece of gear comes from. Item level alone cannot do this: augmented tomestone
/// gear sits at exactly the raid item level, so the augmented set is recognised by id, from what
/// the upgrade trade actually hands out.
/// </summary>
public sealed class GearClassifier
{
    private readonly TierCatalog tiers;
    private readonly ItemCatalog items;

    private HashSet<uint>? augmented;
    private HashSet<uint>? tome;
    private int knownAugmentCount = -1;

    public GearClassifier(TierCatalog tiers, ItemCatalog items)
    {
        this.tiers = tiers;
        this.items = items;
    }

    public GearSource Classify(uint itemId)
    {
        if (itemId == 0)
            return GearSource.None;

        var tier = tiers.Tier;
        EnsureSets(tier);

        if (augmented!.Contains(itemId))
            return GearSource.TomeAugmented;

        if (tome!.Contains(itemId))
            return GearSource.Tome;

        if (!items.TryGetItem(itemId, out var info) || info.Slot == null)
            return GearSource.None;

        // Has to come before the item level check, not after: augmented gear is at exactly the raid
        // item level, so the level says "raid" for both. The name is the only thing that separates
        // them until the upgrade trade has been discovered.
        if (tier.IsAugmentedName(info.Name))
            return GearSource.TomeAugmented;

        if (info.ItemLevel == tier.RaidItemLevel || info.ItemLevel == tier.RaidWeaponItemLevel)
            return GearSource.Raid;

        if (info.ItemLevel == tier.TomeItemLevel)
            return GearSource.Tome;

        // Anything else costs the raid nothing — crafted, an older tier, a relic. The planner only
        // ever asks "does this need a drop", so they all collapse into one answer.
        return GearSource.Crafted;
    }

    /// <summary>The side whose material an augmented piece consumes, for filling in need lists.</summary>
    public GearSide? SideOfAugment(uint itemId) =>
        tiers.Tier.Augments.FirstOrDefault(a => a.AugmentedItemId == itemId)?.Side;


    private void EnsureSets(TierDefinition tier)
    {
        // Cheap staleness check: rediscovering the tier replaces the whole list.
        if (augmented != null && tome != null && knownAugmentCount == tier.Augments.Count)
            return;

        augmented = tier.Augments.Select(a => a.AugmentedItemId).ToHashSet();
        tome = tier.Augments.Where(a => a.BaseItemId != 0).Select(a => a.BaseItemId).ToHashSet();
        knownAugmentCount = tier.Augments.Count;
    }
}
