using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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
                Load(config.ActiveTierId);

            config.Tier ??= new TierDefinition { Id = config.ActiveTierId };

            if (!resolved)
                Resolve();

            return config.Tier;
        }
    }

    /// <summary>Tiers that came with the plugin. Replaced wholesale on every update.</summary>
    private static string ShippedTierDirectory =>
        Path.Combine(Services.PluginInterface.AssemblyLocation.Directory?.FullName ?? ".", "Data", "Tiers");

    /// <summary>
    /// Tiers built in game. Kept next to the config rather than next to the assembly, so a rebuild
    /// or a plugin update cannot take them with it.
    /// </summary>
    private static string UserTierDirectory =>
        Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(), "Tiers");

    /// <summary>
    /// Enum values are written by name. These files get hand-edited and passed around, and
    /// <c>"Side": 2</c> tells nobody anything.
    /// </summary>
    private static readonly JsonSerializerSettings FileFormat = new()
    {
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() },
    };

    /// <summary>
    /// Tiers on disk, by file id and readable name. The name is read out of each file rather than
    /// derived from its filename, so the list shows what the tier calls itself.
    /// </summary>
    public static List<(string Id, string Name)> AvailableTiers()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Shipped first, so a tier edited in game shadows the one it started from.
        foreach (var directory in new[] { ShippedTierDirectory, UserTierDirectory })
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(id))
                    continue;

                found[id] = ReadNameOf(path) ?? id;
            }
        }

        return found.Select(kv => (kv.Key, kv.Value))
                    .OrderBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private static string? ReadNameOf(string path)
    {
        try
        {
            var definition = JsonConvert.DeserializeObject<TierDefinition>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(definition?.Name) ? null : definition!.Name;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"Could not read the name out of {path}.");
            return null;
        }
    }

    /// <summary>A tier built in game wins over one that shipped under the same id.</summary>
    private static string? PathFor(string id)
    {
        var user = Path.Combine(UserTierDirectory, $"{id}.json");
        if (File.Exists(user))
            return user;

        var shipped = Path.Combine(ShippedTierDirectory, $"{id}.json");
        return File.Exists(shipped) ? shipped : null;
    }

    /// <summary>
    /// File name for a tier, derived from what it is called. Keeping the id out of the user's hands
    /// removes a field that could only ever be got wrong.
    /// </summary>
    public static string SlugFor(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else if (c is ' ' or '-' or '_' && builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        return builder.ToString().Trim('-') is { Length: > 0 } slug ? slug : "tier";
    }

    /// <summary>
    /// Replaces the active tier with one from disk, shipped or built in game. Also the "I broke it,
    /// start over" button, which is why it drops every edit rather than merging.
    /// </summary>
    public bool Load(string id)
    {
        var path = PathFor(id);
        if (path == null)
            return false;

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
    /// Writes the active tier out as json so it can be reloaded, kept, or handed to someone else.
    /// This is what makes a tier built in game as durable as one that shipped.
    /// </summary>
    public string SaveToFile()
    {
        var tier = Tier;

        if (string.IsNullOrWhiteSpace(tier.Name))
            return "Give the tier a name first.";

        tier.Id = SlugFor(tier.Name);
        config.ActiveTierId = tier.Id;

        try
        {
            Directory.CreateDirectory(UserTierDirectory);

            var path = Path.Combine(UserTierDirectory, $"{tier.Id}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(tier, FileFormat));
            config.Save();

            Services.Log.Information($"Wrote tier definition to {path}");
            return $"Saved \"{tier.Name}\".";
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not write the tier definition.");
            return ex.Message;
        }
    }

    /// <summary>
    /// A blank tier with the usual four fights and three materials, ready to have its books and
    /// item levels filled in. Everything before this had to be typed into a json file by hand.
    /// </summary>
    public void CreateBlank()
    {
        var tier = new TierDefinition
        {
            Id = "new-tier",
            Name = "New tier",
            Upgrades =
            {
                new TierUpgrade { Side = GearSide.Right },
                new TierUpgrade { Side = GearSide.Left },
                new TierUpgrade { Side = GearSide.Weapon },
            },
        };

        for (var index = 1; index <= PlayerPlanEncounters; index++)
        {
            tier.Encounters.Add(new TierEncounter
            {
                Index = index,
                Name = $"Fight {index}",
                DropCount = 2,
            });
        }

        config.ActiveTierId = tier.Id;
        config.Tier = tier;
        resolved = false;
        config.Save();
    }

    /// <summary>Four, in every tier the game has shipped.</summary>
    private const int PlayerPlanEncounters = 4;

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

    /// <summary>Addons a token exchange can be sitting in, most likely first.</summary>
    private static readonly string[] ShopAddons =
        ["ShopExchangeCurrency", "ShopExchangeItem", "InclusionShop", "Shop", "FreeShop"];

    /// <summary>The lowest item id worth treating as real, to keep small counters out of the scan.</summary>
    private const uint PlausibleItemId = 1000;

    /// <summary>
    /// Reads the tier's books off an exchange window that is open in front of you.
    ///
    /// Standing at the NPC is by far the most reliable way to say which shop this tier means —
    /// better than any name matching, because the game is already showing exactly the right one.
    /// The window's contents are matched against <c>SpecialShop</c> rows, and the currencies those
    /// rows charge are the books.
    /// </summary>
    public string DiscoverBooksFromOpenShop()
    {
        var visible = ReadOpenShopItemIds();
        if (visible.Count == 0)
            return string.Empty;

        var rows = MatchingShopRows(visible);
        if (rows.Count == 0)
            return "A shop is open, but none of its entries matched the game's shop data.";

        var books = CostItemsOf(rows);
        if (books.Count == 0)
            return "A shop is open, but nothing in it is paid for with a token.";

        var tier = Tier;
        var encounters = tier.Encounters.OrderBy(e => e.Index).ToList();

        if (encounters.Count == 0)
            return "No fights to put those books on — add some first.";

        // Books are created in fight order, so ascending id is the right guess for I, II, III, IV.
        // It is a guess, which is why it is reported and every one is still editable.
        var ordered = books.OrderBy(id => id).Take(encounters.Count).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            encounters[i].TokenItemId = ordered[i];
            encounters[i].TokenItemName = items.GetItemName(ordered[i]);
        }

        config.Save();

        var named = string.Join(", ", ordered.Select((id, i) => $"{encounters[i].Name} = {items.GetItemName(id)}"));
        return $"Read {ordered.Count} book(s) from the open shop: {named}.";
    }

    private List<uint> ReadOpenShopItemIds()
    {
        foreach (var name in ShopAddons)
        {
            var addon = Services.GameGui.GetAddonByName(name);
            if (addon.IsNull || !addon.IsVisible)
                continue;

            var found = new List<uint>();

            foreach (var value in addon.AtkValues)
            {
                if (!value.TryGet<uint>(out var id) || id < PlausibleItemId)
                    continue;

                if (items.TryGetItem(id, out var info) && !string.IsNullOrEmpty(info.Name))
                    found.Add(id);
            }

            if (found.Count > 0)
                return found.Distinct().ToList();
        }

        return [];
    }

    /// <summary>
    /// Shop rows that visibly overlap with what is on screen. Several rows can belong to one NPC,
    /// so this takes every row that shares more than a couple of entries rather than just the best.
    /// </summary>
    private static List<SpecialShop> MatchingShopRows(IReadOnlyCollection<uint> visible)
    {
        const int minimumOverlap = 3;

        var onScreen = visible.ToHashSet();
        var result = new List<SpecialShop>();

        foreach (var shop in Services.Data.GetExcelSheet<SpecialShop>())
        {
            var overlap = 0;

            foreach (var entry in shop.Item)
            {
                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId != 0 && onScreen.Contains(receive.Item.RowId))
                        overlap++;
                }
            }

            if (overlap >= minimumOverlap)
                result.Add(shop);
        }

        return result;
    }

    private static List<uint> CostItemsOf(IEnumerable<SpecialShop> rows)
    {
        var costs = new HashSet<uint>();

        foreach (var shop in rows)
        {
            foreach (var entry in shop.Item)
            {
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.ItemCost.RowId != 0 && cost.CurrencyCost > 0)
                        costs.Add(cost.ItemCost.RowId);
                }
            }
        }

        return costs.ToList();
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

        // Books bought with other books. This is where most of the last fight's shop goes, and
        // reading it as gear is what made every one of those rows come out as "Weapon".
        var trades = new Dictionary<(int From, int To), int>();

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

                        // Another book: a trade, not a reward.
                        if (tokens.TryGetValue(rewardId, out var toEncounter))
                        {
                            if (toEncounter == encounterIndex)
                                continue;

                            var key = (encounterIndex, toEncounter);
                            var ratio = Math.Max(1, (int)cost.CurrencyCost);

                            if (!trades.TryGetValue(key, out var known) || ratio < known)
                                trades[key] = ratio;

                            continue;
                        }

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

        ApplyTrades(tier, trades);
        DiscoverAugments(tier);
        DeriveItemLevels(tier);
        config.Save();

        Services.Log.Information(
            $"Discovered {tier.Rewards.Count} exchange entries and {tier.Augments.Count} augmented pieces for {tier.Name}.");

        return tier.Rewards.Count;
    }

    /// <summary>
    /// Works out the tier's four item levels from what the discovery already read. These are the
    /// numbers gear classification runs on, so having to type them in was the last thing standing
    /// between a shop window and a working tier.
    ///
    /// Measured where possible: augmented tomestone gear is equippable and carries the raid item
    /// level by definition, and the plain piece it is traded from carries the tomestone level. The
    /// weapon level comes off the tier's own weapon if one turned up. Anything still missing falls
    /// back to the usual relationships — weapon is raid + 5, tomestone is raid - 10.
    /// </summary>
    private void DeriveItemLevels(TierDefinition tier)
    {
        var augmentedArmour = tier.Augments
                                  .Where(a => a.Slot != null && a.Slot != GearSlot.Weapon)
                                  .Select(a => items.GetItem(a.AugmentedItemId).ItemLevel)
                                  .Where(level => level > 0)
                                  .ToList();

        if (augmentedArmour.Count == 0)
            return;

        // The mode rather than the max: one stray item should not move the whole tier.
        var raid = augmentedArmour.GroupBy(l => l).OrderByDescending(g => g.Count()).First().Key;

        tier.RaidItemLevel = raid;
        tier.AugmentedItemLevel = raid;

        var weapons = tier.Augments
                          .Where(a => a.Slot == GearSlot.Weapon)
                          .Select(a => items.GetItem(a.AugmentedItemId).ItemLevel)
                          .Where(level => level > raid)
                          .ToList();

        tier.RaidWeaponItemLevel = weapons.Count > 0 ? weapons.Max() : (ushort)(raid + 5);

        var baseArmour = tier.Augments
                             .Where(a => a.BaseItemId != 0 && a.Slot != null && a.Slot != GearSlot.Weapon)
                             .Select(a => items.GetItem(a.BaseItemId).ItemLevel)
                             .Where(level => level is > 0 && level < raid)
                             .ToList();

        tier.TomeItemLevel = baseArmour.Count > 0
                                 ? baseArmour.GroupBy(l => l).OrderByDescending(g => g.Count()).First().Key
                                 : (ushort)(raid - 10);

        Services.Log.Information(
            $"Item levels for {tier.Name}: raid {tier.RaidItemLevel}, weapon {tier.RaidWeaponItemLevel}, " +
            $"tome {tier.TomeItemLevel}, augmented {tier.AugmentedItemLevel}.");
    }

    /// <summary>
    /// Folds the book-for-book rows into conversions. Discovering these matters as much as the
    /// costs do: which books a player can actually spend decides whether they are stuck.
    /// </summary>
    private static void ApplyTrades(TierDefinition tier, Dictionary<(int From, int To), int> trades)
    {
        if (trades.Count == 0)
            return;

        var conversions = new List<TierTokenConversion>();

        foreach (var group in trades.GroupBy(t => t.Key.From))
        {
            conversions.Add(new TierTokenConversion
            {
                FromEncounter = group.Key,
                ToEncounters = group.Select(t => t.Key.To).Distinct().OrderBy(e => e).ToList(),
                Ratio = group.Min(t => t.Value),
            });
        }

        tier.Conversions = conversions.OrderBy(c => c.FromEncounter).ToList();
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

        reward.Slot = Slots.SlotFromName(reward.ItemName, Slots.All);

        // Nothing is guessed from the fight's drop pool any more. The last fight drops exactly one
        // slot, so "if the pool has one entry, use it" quietly stamped Weapon onto every mount,
        // minion and orchestrion roll its books also buy. Unassigned is the honest answer, and the
        // table below is one click per row to fix.
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

        if (!items.TryGetItem(itemId, out var info))
            return false;

        // Gear that drops as itself.
        if (info.Slot != null && tier.IsTierItemLevel(info.ItemLevel))
        {
            slot = info.Slot;
            return true;
        }

        // A coffer, which is the normal case and the one the item level test can never answer:
        // coffers have no equip category at all, so they were coming out as "not tier loot" even
        // with the tier set up correctly. Their name says what is inside — "Genji Earring Coffer
        // (IL 340)" — so that is what decides.
        var named = Slots.SlotFromName(info.Name, Slots.All);
        if (named != null)
        {
            slot = named;
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
        return encounter == null ? Slots.All : encounter.DropSlots;
    }
}
