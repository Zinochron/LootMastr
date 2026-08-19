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

    /// <summary>
    /// Two questions, in order: does this item belong to the tier at all, and if so which half.
    /// The item level answers the first, the name answers the second — augmented tomestone gear is
    /// spelled <c>Augmented …</c> and raid gear never is.
    ///
    /// Both come straight from the item sheet, so classification does **not** depend on the
    /// SpecialShop discovery having run or having got everything right. The discovered trade is
    /// only consulted for items the levels do not account for, where it is exact.
    /// </summary>
    public GearSource Classify(uint itemId)
    {
        if (itemId == 0)
            return GearSource.None;

        if (!items.TryGetItem(itemId, out var info) || info.Slot == null)
            return GearSource.None;

        var tier = tiers.Tier;

        var byLevel = tier.ClassifyByLevel(info.ItemLevel, info.Name);
        if (byLevel != null)
            return byLevel.Value;

        // Only for what the levels do not explain — a tier whose levels are not filled in, or an
        // augmented set sitting somewhere unexpected.
        EnsureSets(tier);

        if (augmented!.Contains(itemId))
            return GearSource.TomeAugmented;

        if (tome!.Contains(itemId))
            return GearSource.Tome;

        // Anything else costs the raid nothing, which is all the planner ever asks. But the sheet is
        // read by people, and "Craft" on a relic weapon is a small lie that makes them doubt the
        // rest of the row -- and it hides the case worth seeing, which is gear this tier does not
        // understand.
        //
        // Crafted gear is tradeable and not unique. Relics, older raid gear and most dungeon drops
        // are Unique and Untradeable, which is the line the tooltip already draws.
        return info.IsUnique || info.IsUntradable ? GearSource.Other : GearSource.Crafted;
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
