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
    bool IsUntradable,
    bool IsSoulCrystal = false);

/// <summary>One stat an item carries, as a <c>BaseParam</c> row id and its value.</summary>
public readonly record struct ItemStat(uint BaseParam, int Value);

/// <summary>
/// What a piece of equipment is worth in the damage formula. Kept apart from <see cref="ItemInfo"/>
/// and only for items that go in a slot: three quarters of the item sheet is food, materials and
/// furniture, and none of it has a stat line.
/// </summary>
public sealed record ItemStats(
    uint ItemId,
    ushort PhysicalDamage,
    ushort MagicalDamage,
    ushort DelayMs,
    byte MateriaSlots,
    bool CanBeHq,
    IReadOnlyList<ItemStat> Params,
    IReadOnlyList<ItemStat> HqParams)
{
    /// <summary>The value of one stat, or 0. Linear over at most six entries, so no dictionary.</summary>
    public int ValueOf(uint baseParam)
    {
        foreach (var stat in Params)
        {
            if (stat.BaseParam == baseParam)
                return stat.Value;
        }

        return 0;
    }

    /// <summary>Weapon damage, whichever kind this weapon deals. Zero for everything else.</summary>
    public int WeaponDamage => Math.Max(PhysicalDamage, MagicalDamage);
}

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

    /// <summary>
    /// What this item is worth in the damage formula, for equipment only.
    ///
    /// The values are the item's own, before materia and before the high quality bonus. Nothing
    /// here knows what is melded into a particular copy — that is the caller's business, because it
    /// differs between two people wearing the same piece.
    /// </summary>
    /// <summary>
    /// An item id with the high quality marker taken off.
    ///
    /// The game says "high quality" in two different ways depending on who is asking.
    /// <c>InventoryItem</c> carries the plain id and a flag, which is what reading your own equipped
    /// gear goes through. <c>AgentInspect</c> — and the market board, and glamour data — instead add
    /// <b>one million</b> to the id itself.
    ///
    /// That difference is invisible until somebody wears crafted gear, because crafted gear is the
    /// only kind anyone wears high quality: raid and tomestone pieces have no HQ version at all. So
    /// scanning yourself worked, scanning anybody else silently dropped exactly their crafted
    /// pieces, and the roster showed empty slots for gear the character was visibly wearing.
    ///
    /// Collectables use 500,000 the same way. They are never equipment, so they cannot reach this.
    /// </summary>
    public const uint HighQualityOffset = 1_000_000;

    public static uint BaseItemId(uint itemId) =>
        itemId >= HighQualityOffset ? itemId - HighQualityOffset : itemId;

    public static bool IsHighQualityId(uint itemId) => itemId >= HighQualityOffset;

    public bool TryGetStats(uint itemId, out ItemStats stats) =>
        model.Value.Stats.TryGetValue(itemId, out stats!);

    /// <summary>
    /// What one materia is worth, looked up by the materia's own <b>item</b> id — which is how both
    /// gear planners spell a meld, and the only handle an import gives us.
    ///
    /// The <c>Materia</c> sheet is the other way round: a row per materia type, holding one item id
    /// and one value per grade. This is that table turned inside out.
    /// </summary>
    public bool TryGetMateria(uint itemId, out ItemStat effect) =>
        model.Value.Materia.TryGetValue(itemId, out effect);

    /// <summary>
    /// The materia item a melded slot holds, from whatever the game put in it.
    ///
    /// <c>InventoryItem.Materia</c> carries an id and a grade, and which id it is was never settled:
    /// the stat probe was run on a character with no melds anywhere, so it had nothing to read. Both
    /// readings are therefore tried — the item table first, then the <c>Materia</c> sheet's own row
    /// and grade — and the answer comes back as an item id either way, so nothing downstream has to
    /// know which it was.
    /// </summary>
    public bool TryResolveMeld(ushort id, byte grade, out uint itemId)
    {
        if (model.Value.Materia.ContainsKey(id))
        {
            itemId = id;
            return true;
        }

        return model.Value.MateriaByRow.TryGetValue((id, grade), out itemId);
    }

    private sealed record Model(
        Dictionary<uint, ItemInfo> Items,
        Dictionary<string, uint> ByName,
        Dictionary<uint, ItemStats> Stats,
        Dictionary<uint, ItemStat> Materia,
        Dictionary<(ushort Row, byte Grade), uint> MateriaByRow);

    private static Model Build()
    {
        var itemSheet = Services.Data.GetExcelSheet<Item>();

        // Hoisted: this used to be fetched once per item inside SlotOf, which is forty thousand
        // sheet lookups to answer forty thousand questions about one small table.
        var slotSheet = Services.Data.GetExcelSheet<EquipSlotCategory>();

        var items = new Dictionary<uint, ItemInfo>();
        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var stats = new Dictionary<uint, ItemStats>();

        foreach (var row in itemSheet)
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;

            var slot = SlotOf(slotSheet, row.EquipSlotCategory.RowId);

            items[row.RowId] = new ItemInfo(
                row.RowId,
                name,
                row.Icon,
                slot,
                (ushort)row.LevelItem.RowId,
                row.ClassJobCategory.RowId,
                row.IsUnique,
                row.IsUntradable,
                IsSoulCrystalCategory(slotSheet, row.EquipSlotCategory.RowId));

            if (slot != null)
                stats[row.RowId] = StatsOf(row);

            // Duplicated names exist (mostly across expansions); the first row wins, which is the
            // older item. Tier json therefore always spells out the current, unambiguous name.
            byName.TryAdd(name, row.RowId);
        }

        var (materia, byRow) = BuildMateria();

        Services.Log.Information($"ItemCatalog built: {items.Count} items, {stats.Count} with stats, " +
                                 $"{materia.Count} materia.");

        return new Model(items, byName, stats, materia, byRow);
    }

    /// <summary>
    /// The materia sheet, turned inside out twice.
    ///
    /// It is a row per materia type holding one item and one value per grade. Melds are spelled as
    /// item ids by both gear planners and as something-plus-a-grade by the game, so both ways in are
    /// built: item id → stat, and (row, grade) → item id.
    /// </summary>
    private static (Dictionary<uint, ItemStat>, Dictionary<(ushort, byte), uint>) BuildMateria()
    {
        var byItem = new Dictionary<uint, ItemStat>();
        var byRow = new Dictionary<(ushort, byte), uint>();

        foreach (var row in Services.Data.GetExcelSheet<Materia>())
        {
            var stat = row.BaseParam.RowId;
            if (stat == 0)
                continue;

            var grades = Math.Min(row.Item.Count, row.Value.Count);

            for (var grade = 0; grade < grades; grade++)
            {
                var itemId = row.Item[grade].RowId;
                if (itemId == 0 || row.Value[grade] == 0)
                    continue;

                byItem[itemId] = new ItemStat(stat, row.Value[grade]);
                byRow[((ushort)row.RowId, (byte)grade)] = itemId;
            }
        }

        return (byItem, byRow);
    }

    /// <summary>
    /// Reads an item's stat line. The collections are walked by their own <c>Count</c> rather than
    /// a fixed six — the schema has held at six for years, which is not the same as a guarantee —
    /// and zero-valued entries are dropped, because most items fill only two or three of them.
    /// </summary>
    private static ItemStats StatsOf(Item row)
    {
        return new ItemStats(
            row.RowId,
            row.DamagePhys,
            row.DamageMag,
            row.Delayms,
            row.MateriaSlotCount,
            row.CanBeHq,
            Read(row.BaseParam, row.BaseParamValue),
            Read(row.BaseParamSpecial, row.BaseParamValueSpecial));

        static IReadOnlyList<ItemStat> Read(
            Lumina.Excel.Collection<Lumina.Excel.RowRef<BaseParam>> parameters,
            Lumina.Excel.Collection<short> values)
        {
            var count = Math.Min(parameters.Count, values.Count);
            var result = new List<ItemStat>(count);

            for (var i = 0; i < count; i++)
            {
                var id = parameters[i].RowId;
                if (id == 0 || values[i] == 0)
                    continue;

                result.Add(new ItemStat(id, values[i]));
            }

            return result;
        }
    }

    /// <summary>
    /// Maps an <c>EquipSlotCategory</c> row to a slot. The row ids are read out of the sheet rather
    /// than hardcoded, because a category is defined by which of its columns is set to 1.
    /// </summary>
    /// <summary>
    /// Whether a category is a job's soul crystal.
    ///
    /// Equipment, and not gear anybody plans — so it needs its own answer rather than sharing "no
    /// gear slot" with genuinely unknown items. Every character is wearing one, so without this the
    /// scan reports an unidentifiable item every single time, and a warning that always fires is a
    /// warning nobody reads.
    /// </summary>
    private static bool IsSoulCrystalCategory(Lumina.Excel.ExcelSheet<EquipSlotCategory> sheet, uint categoryId) =>
        categoryId != 0 && sheet.TryGetRow(categoryId, out var c) && c.SoulCrystal > 0;

    private static GearSlot? SlotOf(Lumina.Excel.ExcelSheet<EquipSlotCategory> sheet, uint categoryId)
    {
        if (categoryId == 0)
            return null;

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
