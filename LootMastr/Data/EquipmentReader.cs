using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace LootMastr.Data;

/// <summary>
/// Reads what a character is actually wearing. Two sources, both exact:
///
/// <list type="bullet">
/// <item>The local player, straight out of the equipped inventory container.</item>
/// <item>Anyone else, through the examine window, which reports real item ids in
/// <c>ItemData.ItemId</c> and keeps the glamour separately in <c>GlamourItemId</c> — so a static
/// full of glamours reads correctly, which is the thing that sinks the model-id approach.</item>
/// </list>
/// </summary>
public sealed class EquipmentReader
{
    private readonly ItemCatalog items;

    public EquipmentReader(ItemCatalog items) => this.items = items;

    /// <summary>Equipped items of the local player, keyed by slot.</summary>
    public unsafe Dictionary<GearSlot, uint> ReadLocal()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return [];

        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded)
            return [];

        var found = new List<uint>();

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId != 0)
                found.Add(slot->ItemId);
        }

        return ToSlots(found);
    }

    /// <summary>Equipped items of whoever the examine window is currently showing.</summary>
    public unsafe Dictionary<GearSlot, uint> ReadInspected()
    {
        var agent = AgentInspect.Instance();
        if (agent == null)
            return [];

        var found = new List<uint>();
        var span = agent->Items;

        for (var i = 0; i < span.Length; i++)
        {
            // ItemId, deliberately, not GlamourItemId.
            if (span[i].ItemId != 0)
                found.Add(span[i].ItemId);
        }

        return ToSlots(found);
    }

    /// <summary>Average item level the examine window is reporting, or 0.</summary>
    public unsafe int InspectedItemLevel()
    {
        var state = UIState.Instance();
        return state == null ? 0 : state->Inspect.AverageItemLevel;
    }

    /// <summary>
    /// Average item level worked out from the items themselves.
    ///
    /// Needed only for the local player, and only because the game does not hand it over: the examine
    /// window carries a ready number for anybody else, and nothing in <c>PlayerState</c> or
    /// <c>InventoryManager</c> is the equivalent for you. The scan used to pass 0 for your own row,
    /// which read as "i0" on your own sheet.
    ///
    /// The weapon counts twice, standing in for the off-hand slot. Every job but paladin has nothing
    /// there, and a paladin's shield comes out of the same coffer at the same level — so counting the
    /// main hand for both is right in the one case and harmless in the other. Twelve values over
    /// eleven slots, which is what the character window divides by.
    /// </summary>
    public int AverageItemLevel(IReadOnlyDictionary<GearSlot, uint> gear)
    {
        var total = 0;
        var counted = 0;

        foreach (var slot in Slots.All)
        {
            if (!gear.TryGetValue(slot, out var itemId) || !items.TryGetItem(itemId, out var info))
                continue;

            total += info.ItemLevel;
            counted++;

            if (slot == GearSlot.Weapon)
            {
                total += info.ItemLevel;
                counted++;
            }
        }

        return counted == 0 ? 0 : total / counted;
    }

    /// <summary>
    /// Files a flat list of equipped items by slot, using each item's own equip category rather
    /// than its position — container layouts differ between the inventory and the examine window,
    /// and the item always knows where it goes.
    ///
    /// Rings are the exception, since item data never says which finger. They are handed out in
    /// the order they were read, which is the order the game lists them in.
    /// </summary>
    private Dictionary<GearSlot, uint> ToSlots(IEnumerable<uint> equipped)
    {
        var result = new Dictionary<GearSlot, uint>();
        var rings = new List<uint>();

        foreach (var itemId in equipped)
        {
            if (!items.TryGetItem(itemId, out var info) || info.Slot == null)
                continue;

            // A shield is not tracked separately — it arrives with the weapon and would otherwise
            // sit in the dictionary as a slot nothing ever reads.
            if (info.Slot == GearSlot.OffHand)
                continue;

            if (Slots.IsRing(info.Slot.Value))
            {
                rings.Add(itemId);
                continue;
            }

            result[info.Slot.Value] = itemId;
        }

        if (rings.Count > 0)
            result[GearSlot.Ring1] = rings[0];

        if (rings.Count > 1)
            result[GearSlot.Ring2] = rings[1];

        return result;
    }
}
