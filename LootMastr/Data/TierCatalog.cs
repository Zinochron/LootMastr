using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace LootMastr.Data;

/// <summary>
/// Owns the active <see cref="TierDefinition"/>: loads the shipped json, resolves item names to
/// ids, and discovers the book exchange from the game's own shop data.
/// </summary>
public sealed class TierCatalog
{
    private readonly Configuration config;
    private readonly ItemCatalog items;

    private bool resolved;

    public TierCatalog(Configuration config, ItemCatalog items)
    {
        this.config = config;
        this.items = items;
    }

    /// <summary>
    /// The active tier, resolved against game data on first touch. Resolution walks the item sheet,
    /// so it deliberately does not happen while the plugin is still loading.
    /// </summary>
    public TierDefinition Tier
    {
        get
        {
            if (config.Tier == null)
                LoadShipped(config.ActiveTierId);

            config.Tier ??= new TierDefinition { Id = config.ActiveTierId };

            if (!resolved)
                Resolve();

            return config.Tier;
        }
    }

    private static string TierDirectory =>
        Path.Combine(Services.PluginInterface.AssemblyLocation.Directory?.FullName ?? ".", "Data", "Tiers");

    public static IEnumerable<string> ShippedTierIds()
    {
        if (!Directory.Exists(TierDirectory))
            return [];

        return Directory.EnumerateFiles(TierDirectory, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => id!)
                        .OrderBy(id => id);
    }

    /// <summary>
    /// Replaces the active tier with the shipped defaults. Also the "I broke it, start over"
    /// button, which is why it drops every edit rather than merging.
    /// </summary>
    public bool LoadShipped(string id)
    {
        var path = Path.Combine(TierDirectory, $"{id}.json");

        try
        {
            var definition = JsonConvert.DeserializeObject<TierDefinition>(File.ReadAllText(path));
            if (definition == null)
                return false;

            config.ActiveTierId = id;
            config.Tier = definition;
            resolved = false;
            config.Save();
            return true;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Could not read tier definition {path}.");
            return false;
        }
    }

    /// <summary>
    /// Turns the readable names in the definition into item ids. Anything that fails keeps an id of
    /// 0 and is reported by <see cref="TierDefinition.Problems"/> rather than throwing — a tier
    /// definition written for a later patch should still load and show what is wrong.
    /// </summary>
    public void Resolve()
    {
        var tier = config.Tier;
        if (tier == null)
            return;

        resolved = true;

        foreach (var encounter in tier.Encounters)
            encounter.TokenItemId = items.TryGetIdByName(encounter.TokenItemName, out var id) ? id : 0;

        foreach (var upgrade in tier.Upgrades)
            upgrade.ItemId = items.TryGetIdByName(upgrade.ItemName, out var id) ? id : 0;

        foreach (var reward in tier.Rewards)
        {
            if (reward.ItemId != 0)
                reward.ItemName = items.GetItemName(reward.ItemId);
        }
    }

    /// <summary>
    /// Walks <c>SpecialShop</c> for everything the tier's books buy. This is why the plugin never
    /// asks anyone to type in exchange costs: the shop rows are the same data the game uses, so
    /// they cannot disagree with it.
    /// </summary>
    public int DiscoverRewards()
    {
        var tier = Tier;

        var tokens = tier.Encounters
                         .Where(e => e.TokenItemId != 0)
                         .ToDictionary(e => e.TokenItemId, e => e.Index);

        if (tokens.Count == 0)
        {
            Services.Log.Warning("Cannot discover rewards: no book names resolved to items.");
            return 0;
        }

        // Keyed by reward item so a piece sold by several NPCs collapses to one line.
        var found = new Dictionary<uint, TierReward>();

        foreach (var shop in Services.Data.GetExcelSheet<SpecialShop>())
        {
            foreach (var entry in shop.Item)
            {
                foreach (var cost in entry.ItemCosts)
                {
                    var costItemId = cost.ItemCost.RowId;
                    if (costItemId == 0 || !tokens.TryGetValue(costItemId, out var encounterIndex))
                        continue;

                    if (cost.CurrencyCost == 0)
                        continue;

                    foreach (var receive in entry.ReceiveItems)
                    {
                        var rewardId = receive.Item.RowId;
                        if (rewardId == 0)
                            continue;

                        var reward = new TierReward
                        {
                            Encounter = encounterIndex,
                            Cost = (int)cost.CurrencyCost,
                            ItemId = rewardId,
                            ItemName = items.GetItemName(rewardId),
                        };

                        if (found.TryGetValue(rewardId, out var existing) && existing.Cost <= reward.Cost)
                            continue;

                        found[rewardId] = reward;
                    }
                }
            }
        }

        // Keep whatever the user already assigned; only the costs are re-derived.
        foreach (var reward in found.Values)
        {
            var previous = tier.Rewards.FirstOrDefault(r => r.ItemId == reward.ItemId);
            if (previous != null)
            {
                reward.Slot = previous.Slot;
                reward.Upgrade = previous.Upgrade;
            }

            AutoAssign(tier, reward);
        }

        tier.Rewards = found.Values
                            .OrderBy(r => r.Encounter)
                            .ThenBy(r => r.Cost)
                            .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase)
                            .ToList();

        DiscoverAugments(tier);
        config.Save();

        Services.Log.Information(
            $"Discovered {tier.Rewards.Count} exchange entries and {tier.Augments.Count} augmented pieces for {tier.Name}.");

        return tier.Rewards.Count;
    }

    /// <summary>
    /// Finds the augmented tomestone set by looking for shop entries paid for with an upgrade
    /// material. The other cost of such an entry is the plain tome piece, so one pass yields both
    /// halves of the trade — and neither depends on item names or on the client language.
    /// </summary>
    private void DiscoverAugments(TierDefinition tier)
    {
        var sides = tier.Upgrades
                        .Where(u => u.ItemId != 0)
                        .ToDictionary(u => u.ItemId, u => u.Side);

        if (sides.Count == 0)
            return;

        var found = new Dictionary<uint, TierAugment>();

        foreach (var shop in Services.Data.GetExcelSheet<SpecialShop>())
        {
            foreach (var entry in shop.Item)
            {
                GearSide? side = null;
                uint baseItemId = 0;

                foreach (var cost in entry.ItemCosts)
                {
                    var costItemId = cost.ItemCost.RowId;
                    if (costItemId == 0)
                        continue;

                    if (sides.TryGetValue(costItemId, out var matched))
                        side = matched;
                    else
                        baseItemId = costItemId;
                }

                if (side == null)
                    continue;

                foreach (var receive in entry.ReceiveItems)
                {
                    var augmentedId = receive.Item.RowId;
                    if (augmentedId == 0 || found.ContainsKey(augmentedId))
                        continue;

                    found[augmentedId] = new TierAugment
                    {
                        AugmentedItemId = augmentedId,
                        AugmentedItemName = items.GetItemName(augmentedId),
                        BaseItemId = baseItemId,
                        BaseItemName = baseItemId == 0 ? string.Empty : items.GetItemName(baseItemId),
                        Side = side.Value,
                        Slot = items.GetItem(augmentedId).Slot,
                    };
                }
            }
        }

        tier.Augments = found.Values
                             .OrderBy(a => a.Slot)
                             .ThenBy(a => a.AugmentedItemName, StringComparer.OrdinalIgnoreCase)
                             .ToList();
    }

    /// <summary>
    /// Fills in what can be worked out without asking, best evidence first:
    ///
    /// <list type="number">
    /// <item>An upgrade material, matched by id.</item>
    /// <item>Equippable gear sold directly, which knows its own slot exactly.</item>
    /// <item>A coffer, from the slot written in its own name.</item>
    /// <item>Failing all that, the only slot that fight's book buys, if there is just one.</item>
    /// </list>
    ///
    /// The name is matched against every slot rather than only the ones the tier says that fight
    /// drops. Restricting it sounded safer and mostly just left rows unassigned whenever the tier's
    /// drop pools were slightly off — and a coffer with "Head" in its name is not a ring whatever
    /// the pools claim.
    /// </summary>
    private void AutoAssign(TierDefinition tier, TierReward reward)
    {
        if (reward.IsAssigned)
            return;

        var upgrade = tier.Upgrades.FirstOrDefault(u => u.ItemId == reward.ItemId);
        if (upgrade != null)
        {
            reward.Upgrade = upgrade.Side;
            return;
        }

        // Gear sold as itself rather than as a coffer: no guessing needed.
        if (items.TryGetItem(reward.ItemId, out var info) && info.Slot != null)
        {
            reward.Slot = Slots.CofferSlot(info.Slot.Value);
            return;
        }

        var named = Slots.SlotFromName(reward.ItemName, Slots.All);
        if (named != null)
        {
            reward.Slot = named;
            return;
        }

        var encounter = tier.Encounter(reward.Encounter);
        if (encounter is { DropSlots.Count: 1 })
            reward.Slot = encounter.DropSlots[0];
    }

    /// <summary>
    /// Works out what a loot window entry is worth to the roster. Coffers are recognised from the
    /// exchange table, which lists the same items the chest drops; anything equippable falls back
    /// to its own slot, so a tier that hands out gear directly still works.
    /// </summary>
    public bool TryMatch(uint itemId, out GearSlot? slot, out GearSide? upgrade)
    {
        slot = null;
        upgrade = null;

        if (itemId == 0)
            return false;

        var tier = Tier;

        var reward = tier.Rewards.FirstOrDefault(r => r.ItemId == itemId && r.IsAssigned);
        if (reward != null)
        {
            slot = reward.Slot;
            upgrade = reward.Upgrade;
            return true;
        }

        var material = tier.Upgrades.FirstOrDefault(u => u.ItemId == itemId);
        if (material != null)
        {
            upgrade = material.Side;
            return true;
        }

        if (items.TryGetItem(itemId, out var info) && info.Slot != null &&
            (info.ItemLevel == tier.RaidItemLevel || info.ItemLevel == tier.RaidWeaponItemLevel))
        {
            slot = info.Slot;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Remembers which zone a fight is in, learned from what its chest contained rather than
    /// shipped as a table — territory ids change every tier and a wrong one is invisible.
    /// </summary>
    public void LearnTerritory(int encounterIndex, uint territoryId)
    {
        var encounter = Tier.Encounter(encounterIndex);
        if (encounter == null || territoryId == 0 || encounter.TerritoryId == territoryId)
            return;

        encounter.TerritoryId = territoryId;
        config.Save();
        Services.Log.Information($"Learned territory {territoryId} for {encounter.Name}.");
    }

    public TierEncounter? EncounterInTerritory(uint territoryId) =>
        territoryId == 0 ? null : Tier.Encounters.FirstOrDefault(e => e.TerritoryId == territoryId);

    /// <summary>Candidate slots for a reward, based on what its book's fight drops.</summary>
    public IEnumerable<GearSlot> CandidateSlots(TierReward reward)
    {
        var encounter = Tier.Encounter(reward.Encounter);
        if (encounter == null)
            return Slots.All;

        // Weapon books also buy the shield, which no fight drops on its own.
        return encounter.DropSlots.Contains(GearSlot.Weapon)
                   ? encounter.DropSlots.Append(GearSlot.OffHand)
                   : encounter.DropSlots;
    }
}
