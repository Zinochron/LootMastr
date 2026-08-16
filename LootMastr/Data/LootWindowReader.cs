using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootMastr.Data;

/// <summary>One row of the loot window, with what LootMastr makes of it.</summary>
public readonly record struct LiveLootItem(
    int Index,
    uint ItemId,
    string Name,
    uint IconId,
    int Count,
    RollState RollState,
    RollResult RollResult,
    LootMode Mode,
    bool WeeklyLootItem,
    float SecondsLeft,
    GearSlot? Slot,
    GearSide? Upgrade)
{
    /// <summary>True when this is something the roster could actually be planning around.</summary>
    public bool IsTierLoot => Slot != null || Upgrade != null;

    public bool Decided => RollResult is RollResult.Awarded or RollResult.Passed;

    public string What => Upgrade != null ? $"{Upgrade} upgrade" : Slot?.Label() ?? Name;
}

/// <summary>
/// Reads the loot window. Purely observational — every write path lives in <c>Automation</c>.
///
/// The list comes out of the <c>NeedGreed</c> addon's values rather than out of
/// <c>Loot.Instance()->Items</c>. That was the first version and it was wrong in the one moment
/// that matters: while the leader still gets to choose between assigning an item and putting it up
/// for greed, <c>Loot.Items</c> is empty. It only fills once the choice has been made, so the
/// plugin saw nothing until it was already too late to act. The addon has the whole chest from the
/// moment it opens.
/// </summary>
public sealed class LootWindowReader
{
    /// <summary>
    /// Values before the first item: five numbers, an unknown, and the loot rule caption.
    /// Only used as a fast path — the scan below does not depend on it.
    /// </summary>
    private const int HeaderValues = 7;

    private const int ValuesPerItem = 8;
    private const int NameOffset = 4;
    private const int CountOffset = 5;

    private readonly ItemCatalog items;
    private readonly TierCatalog tiers;

    public LootWindowReader(ItemCatalog items, TierCatalog tiers)
    {
        this.items = items;
        this.tiers = tiers;
    }

    public unsafe bool WindowOpen
    {
        get
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLoot.Instance();
            return agent != null && agent->IsAddonShown() && agent->NumItems > 0;
        }
    }

    /// <summary>
    /// The party's loot rule, straight from the duty state rather than from the caption in the
    /// window, which is localised.
    /// </summary>
    public unsafe bool LootmasterActive()
    {
        var finder = ContentsFinder.Instance();
        return finder != null && finder->LootRules == ContentsFinder.LootRule.Lootmaster;
    }

    public List<LiveLootItem> Read()
    {
        var entries = ReadAddonEntries();
        if (entries.Count == 0)
            return [];

        // Roll state only exists once the game has moved the chest into rolling, so it is read
        // separately and treated as absent until then.
        var states = ReadRollStates();
        var result = new List<LiveLootItem>(entries.Count);

        foreach (var entry in entries)
        {
            var info = items.GetItem(entry.ItemId);
            tiers.TryMatch(entry.ItemId, out var slot, out var upgrade);

            states.TryGetValue(entry.ItemId, out var state);

            result.Add(new LiveLootItem(
                           entry.Index,
                           entry.ItemId,
                           string.IsNullOrEmpty(info.Name) ? entry.Name : info.Name,
                           info.IconId,
                           entry.Count,
                           state.RollState,
                           state.RollResult,
                           state.Mode,
                           state.Weekly,
                           state.Time,
                           slot,
                           upgrade));
        }

        return result;
    }

    private readonly record struct AddonEntry(int Index, uint ItemId, string Name, int Count);

    /// <summary>
    /// Pulls the chest out of the addon's values. The blocks are found by their own shape — an item
    /// id whose name appears four values later — rather than by a fixed offset, so a layout change
    /// costs nothing. The known offsets are only the fallback.
    /// </summary>
    private List<AddonEntry> ReadAddonEntries()
    {
        var addon = Services.GameGui.GetAddonByName("NeedGreed");
        if (addon.IsNull)
            return [];

        var values = addon.AtkValues.ToList();
        if (values.Count == 0)
            return [];

        var found = new List<AddonEntry>();

        for (var i = 0; i + NameOffset < values.Count; i++)
        {
            if (!TryItemId(values[i], out var itemId))
                continue;

            if (!items.TryGetItem(itemId, out var info) || string.IsNullOrEmpty(info.Name))
                continue;

            var name = AsString(values[i + NameOffset]);
            if (!string.Equals(name, info.Name, StringComparison.Ordinal))
                continue;

            var count = i + CountOffset < values.Count && values[i + CountOffset].TryGet<int>(out var c) ? c : 1;

            found.Add(new AddonEntry(found.Count, itemId, info.Name, Math.Max(1, count)));
            i += ValuesPerItem - 1;
        }

        return found.Count > 0 ? found : ReadByFixedLayout(values);
    }

    /// <summary>
    /// Used when nothing matched by shape — an item the catalogue does not know, or a renamed one.
    /// Trusts the offsets seen in the wild instead.
    /// </summary>
    private List<AddonEntry> ReadByFixedLayout(IReadOnlyList<Dalamud.Game.NativeWrapper.AtkValuePtr> values)
    {
        var found = new List<AddonEntry>();

        for (var i = HeaderValues; i + ValuesPerItem <= values.Count; i += ValuesPerItem)
        {
            if (!TryItemId(values[i], out var itemId))
                continue;

            var name = AsString(values[i + NameOffset]) ?? string.Empty;
            var count = values[i + CountOffset].TryGet<int>(out var c) ? c : 1;

            found.Add(new AddonEntry(found.Count, itemId, name, Math.Max(1, count)));
        }

        return found;
    }

    private readonly record struct RollInfo(
        RollState RollState, RollResult RollResult, LootMode Mode, bool Weekly, float Time);

    private static unsafe Dictionary<uint, RollInfo> ReadRollStates()
    {
        var result = new Dictionary<uint, RollInfo>();

        var loot = Loot.Instance();
        if (loot == null)
            return result;

        var span = loot->Items;

        for (var i = 0; i < span.Length; i++)
        {
            var entry = span[i];
            if (entry.ItemId == 0)
                continue;

            result[entry.ItemId] = new RollInfo(entry.RollState, entry.RollResult, entry.LootMode,
                                                entry.WeeklyLootItem, entry.Time);
        }

        return result;
    }

    private static bool TryItemId(Dalamud.Game.NativeWrapper.AtkValuePtr value, out uint itemId)
    {
        itemId = 0;

        if (value.TryGet<uint>(out var unsigned) && unsigned is > 0 and < 1_000_000)
        {
            itemId = unsigned;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Names arrive as either a plain or a managed string depending on whether the game had it
    /// cached, so the type is not worth branching on — both stringify the same.
    /// </summary>
    private static string? AsString(Dalamud.Game.NativeWrapper.AtkValuePtr value) =>
        value.GetValue()?.ToString();
}
