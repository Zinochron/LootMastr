using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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
/// </summary>
public sealed class LootWindowReader
{
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
            var agent = AgentLoot.Instance();
            return agent != null && agent->IsAddonShown() && agent->NumItems > 0;
        }
    }

    public unsafe List<LiveLootItem> Read()
    {
        var result = new List<LiveLootItem>(8);

        var loot = Loot.Instance();
        if (loot == null)
            return result;

        var span = loot->Items;

        for (var i = 0; i < span.Length; i++)
        {
            var entry = span[i];
            if (entry.ItemId == 0 || entry.RollState == RollState.Unavailable)
                continue;

            tiers.TryMatch(entry.ItemId, out var slot, out var upgrade);
            var info = items.GetItem(entry.ItemId);

            result.Add(new LiveLootItem(
                           i,
                           entry.ItemId,
                           info.Name,
                           info.IconId,
                           entry.ItemCount,
                           entry.RollState,
                           entry.RollResult,
                           entry.LootMode,
                           entry.WeeklyLootItem,
                           entry.Time,
                           slot,
                           upgrade));
        }

        return result;
    }

    /// <summary>
    /// Whether the party is on the Lootmaster rule, judged from the items themselves rather than
    /// from a party setting: the loot window is the thing that actually decides.
    /// </summary>
    public bool LootmasterActive(IReadOnlyList<LiveLootItem> loot)
    {
        foreach (var item in loot)
        {
            if (item.Mode == LootMode.LootMasterGreedOnly)
                return true;
        }

        return false;
    }
}
