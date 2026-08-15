using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace LootMastr.Data;

public readonly record struct ItemInfo(
    uint ItemId,
    string Name,
    uint IconId,
    GearSlot? Slot,
    ushort ItemLevel,
    uint ClassJobCategoryId,
    bool IsUnique,
    bool IsUntradable);

/// <summary>
/// Static game data about items, read once from Lumina. Walking the whole item sheet is not free,
/// so it happens lazily on first use rather than during plugin construction.
/// </summary>
public sealed class ItemCatalog
{
    private readonly Lazy<Model> model;

    public ItemCatalog() => model = new Lazy<Model>(Build, isThreadSafe: true);

    public bool IsBuilt => model.IsValueCreated;

    public bool TryGetItem(uint itemId, out ItemInfo info) => model.Value.Items.TryGetValue(itemId, out info);

    public ItemInfo GetItem(uint itemId) =>
        model.Value.Items.TryGetValue(itemId, out var info)
            ? info
            : new ItemInfo(itemId, $"Unknown item #{itemId}", 0, null, 0, 0, false, false);

    public string GetItemName(uint itemId) => GetItem(itemId).Name;

    /// <summary>
    /// Exact, case insensitive name lookup. This is how tier definitions turn readable json into
    /// item ids, so a typo surfaces as an unresolved entry rather than as silently wrong behaviour.
    /// </summary>
    public bool TryGetIdByName(string name, out uint itemId)
    {
        itemId = 0;
        return !string.IsNullOrWhiteSpace(name) && model.Value.ByName.TryGetValue(name.Trim(), out itemId);
    }

    /// <summary>Case insensitive substring search over item names, best (shortest) matches first.</summary>
    public IEnumerable<ItemInfo> Search(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return model.Value.Items.Values
                    .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Name.Length)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(limit);
    }

    /// <summary>Every equippable item at exactly this item level, used to recognise a tier's gear sets.</summary>
    public IEnumerable<ItemInfo> EquipmentAtItemLevel(ushort itemLevel) =>
        model.Value.Items.Values.Where(i => i.Slot != null && i.ItemLevel == itemLevel);

    private sealed record Model(
        Dictionary<uint, ItemInfo> Items,
        Dictionary<string, uint> ByName);

    private static Model Build()
    {
        var itemSheet = Services.Data.GetExcelSheet<Item>();

        var items = new Dictionary<uint, ItemInfo>();
        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in itemSheet)
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;

            items[row.RowId] = new ItemInfo(
                row.RowId,
                name,
                row.Icon,
                SlotOf(row.EquipSlotCategory.RowId),
                (ushort)row.LevelItem.RowId,
                row.ClassJobCategory.RowId,
                row.IsUnique,
                row.IsUntradable);

            // Duplicated names exist (mostly across expansions); the first row wins, which is the
            // older item. Tier json therefore always spells out the current, unambiguous name.
            byName.TryAdd(name, row.RowId);
        }

        Services.Log.Information($"ItemCatalog built: {items.Count} items.");

        return new Model(items, byName);
    }

    /// <summary>
    /// Maps an <c>EquipSlotCategory</c> row to a slot. The row ids are read out of the sheet rather
    /// than hardcoded, because a category is defined by which of its columns is set to 1.
    /// </summary>
    private static GearSlot? SlotOf(uint categoryId)
    {
        if (categoryId == 0)
            return null;

        var sheet = Services.Data.GetExcelSheet<EquipSlotCategory>();
        if (!sheet.TryGetRow(categoryId, out var c))
            return null;

        if (c.MainHand > 0) return GearSlot.Weapon;
        if (c.OffHand > 0) return GearSlot.OffHand;
        if (c.Head > 0) return GearSlot.Head;
        if (c.Body > 0) return GearSlot.Body;
        if (c.Gloves > 0) return GearSlot.Hands;
        if (c.Legs > 0) return GearSlot.Legs;
        if (c.Feet > 0) return GearSlot.Feet;
        if (c.Ears > 0) return GearSlot.Earrings;
        if (c.Neck > 0) return GearSlot.Necklace;
        if (c.Wrists > 0) return GearSlot.Bracelets;

        // Item data never says which of the two finger slots a ring belongs in, so both map to Ring1.
        if (c.FingerR > 0 || c.FingerL > 0) return GearSlot.Ring1;

        return null;
    }
}
