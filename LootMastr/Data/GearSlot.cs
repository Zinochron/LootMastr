using System;
using System.Collections.Generic;
using System.Linq;

namespace LootMastr.Data;

/// <summary>
/// The equipment slots a raid gear set is built from. Belts are gone from the game and soul
/// crystals are not gear, so neither has a slot here.
/// </summary>
public enum GearSlot
{
    Weapon,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Earrings,
    Necklace,
    Bracelets,
    Ring1,
    Ring2,
}

/// <summary>
/// Which upgrade material a tomestone piece in this slot needs. The raid drops one material per
/// side, so this is what decides which encounter a player has to clear for a given slot.
/// </summary>
public enum GearSide
{
    Weapon,
    Left,
    Right,
}

/// <summary>Where a player intends to get the piece for one slot.</summary>
public enum GearSource
{
    /// <summary>Not decided yet, or the slot is irrelevant to this player.</summary>
    None,

    /// <summary>Straight out of a savage coffer. Needs a drop or enough books.</summary>
    Raid,

    /// <summary>Plain tomestone gear. Costs no raid resource, so distribution ignores it.</summary>
    Tome,

    /// <summary>Tomestone gear plus an upgrade material, which does come out of the raid.</summary>
    TomeAugmented,

    /// <summary>Crafted. Costs no raid resource.</summary>
    Crafted,
}

public static class Slots
{
    /// <summary>Every slot, in the order a gear set is normally read.</summary>
    public static readonly GearSlot[] All =
    [
        GearSlot.Weapon, GearSlot.OffHand,
        GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs, GearSlot.Feet,
        GearSlot.Earrings, GearSlot.Necklace, GearSlot.Bracelets, GearSlot.Ring1, GearSlot.Ring2,
    ];

    public static GearSide SideOf(GearSlot slot) => slot switch
    {
        GearSlot.Weapon or GearSlot.OffHand => GearSide.Weapon,
        GearSlot.Head or GearSlot.Body or GearSlot.Hands or GearSlot.Legs or GearSlot.Feet => GearSide.Left,
        _ => GearSide.Right,
    };

    public static bool IsRing(GearSlot slot) => slot is GearSlot.Ring1 or GearSlot.Ring2;

    /// <summary>
    /// The slot a coffer is filed under. Both ring slots share one coffer, and item data never
    /// distinguishes the two, so <see cref="GearSlot.Ring1"/> stands in for either.
    /// </summary>
    public static GearSlot CofferSlot(GearSlot slot) => IsRing(slot) ? GearSlot.Ring1 : slot;

    public static string Label(this GearSlot slot) => slot switch
    {
        GearSlot.Weapon => "Weapon",
        GearSlot.OffHand => "Shield",
        GearSlot.Head => "Head",
        GearSlot.Body => "Body",
        GearSlot.Hands => "Hands",
        GearSlot.Legs => "Legs",
        GearSlot.Feet => "Feet",
        GearSlot.Earrings => "Earrings",
        GearSlot.Necklace => "Necklace",
        GearSlot.Bracelets => "Bracelets",
        GearSlot.Ring1 => "Ring 1",
        GearSlot.Ring2 => "Ring 2",
        _ => slot.ToString(),
    };

    /// <summary>Short label for dense tables, where a column is barely wider than the text.</summary>
    public static string ShortLabel(this GearSlot slot) => slot switch
    {
        GearSlot.Weapon => "Wep",
        GearSlot.OffHand => "Shd",
        GearSlot.Head => "Head",
        GearSlot.Body => "Body",
        GearSlot.Hands => "Hand",
        GearSlot.Legs => "Legs",
        GearSlot.Feet => "Feet",
        GearSlot.Earrings => "Ear",
        GearSlot.Necklace => "Neck",
        GearSlot.Bracelets => "Wrist",
        GearSlot.Ring1 => "Ring1",
        GearSlot.Ring2 => "Ring2",
        _ => slot.ToString(),
    };

    public static string Label(this GearSource source) => source switch
    {
        GearSource.None => "—",
        GearSource.Raid => "Raid",
        GearSource.Tome => "Tome",
        GearSource.TomeAugmented => "Tome+",
        GearSource.Crafted => "Craft",
        _ => source.ToString(),
    };

    public static string Description(this GearSource source) => source switch
    {
        GearSource.None => "Not planned. Ignored when handing out loot.",
        GearSource.Raid => "Comes out of a savage coffer, or is bought with that fight's books.",
        GearSource.Tome => "Plain tomestone gear. Needs nothing from the raid.",
        GearSource.TomeAugmented => "Tomestone gear plus an upgrade material, which does drop in the raid.",
        GearSource.Crafted => "Crafted. Needs nothing from the raid.",
        _ => string.Empty,
    };

    /// <summary>True when satisfying this slot consumes something the raid hands out.</summary>
    public static bool NeedsRaidResource(this GearSource source) =>
        source is GearSource.Raid or GearSource.TomeAugmented;

    public static IEnumerable<GearSource> SelectableSources()
    {
        yield return GearSource.None;
        yield return GearSource.Raid;
        yield return GearSource.TomeAugmented;
        yield return GearSource.Tome;
        yield return GearSource.Crafted;
    }

    /// <summary>
    /// Words a coffer's name carries for each slot. Gear coffers are named after what is in them —
    /// "Grand Champion's Head Gear Coffer (IL 790)" — so the slot can simply be read off.
    ///
    /// Order matters and is not alphabetical: "earring" contains "ring", so the accessories are
    /// tested before the ring and the first hit wins.
    /// </summary>
    private static readonly (GearSlot Slot, string[] Words)[] SlotWords =
    [
        (GearSlot.Earrings, ["earring", "ear cuff", "earclasp"]),
        (GearSlot.Necklace, ["necklace", "neckband", "choker", "neck"]),
        (GearSlot.Bracelets, ["bracelet", "wrist", "armillae", "bangle"]),
        (GearSlot.Ring1, ["ring"]),
        (GearSlot.Head, ["head"]),
        (GearSlot.Body, ["chest", "body"]),
        (GearSlot.Hands, ["hand", "glove"]),
        (GearSlot.Legs, ["leg"]),
        (GearSlot.Feet, ["foot", "feet", "boot"]),
        (GearSlot.OffHand, ["shield", "off hand", "offhand"]),
        (GearSlot.Weapon, ["weapon"]),
    ];

    /// <summary>
    /// Reads the slot out of a coffer's name, restricted to the slots that are plausible for it —
    /// so a bad read can only ever land on a neighbour the same book already buys, never somewhere
    /// unrelated. Null when the name says nothing useful.
    /// </summary>
    public static GearSlot? SlotFromName(string name, IReadOnlyCollection<GearSlot> candidates)
    {
        if (string.IsNullOrWhiteSpace(name) || candidates.Count == 0)
            return null;

        foreach (var (slot, words) in SlotWords)
        {
            var coffer = CofferSlot(slot);
            if (!candidates.Contains(coffer) && !candidates.Contains(slot))
                continue;

            foreach (var word in words)
            {
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return coffer;
            }
        }

        return null;
    }
}
