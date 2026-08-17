using System;
using System.Collections.Generic;
using LootMastr.Data;

namespace LootMastr.Roster;

/// <summary>
/// Members are keyed by name and world rather than by content id: the roster is filled in long
/// before everyone has been in a party together, usually by typing names in.
/// </summary>
public static class RosterKey
{
    public static string For(string name, string world) =>
        string.IsNullOrWhiteSpace(world) ? name.Trim() : $"{name.Trim()}@{world.Trim()}";
}

/// <summary>How a slot reads once target and actual are both known.</summary>
public enum SlotState
{
    /// <summary>Costs the raid nothing — plain tome, crafted, or nothing planned.</summary>
    NotPlanned,

    /// <summary>Planned and still owed.</summary>
    Needed,

    /// <summary>Handed over and being worn. Done in every sense.</summary>
    Done,

    /// <summary>
    /// Handed over but not on the character. Still done as far as handing out loot goes — it must
    /// never come up for assignment again — but the player has something left to do.
    /// </summary>
    AssignedNotWorn,
}

/// <summary>
/// What one player wants in one slot (the target), what they were given, and what they are
/// actually wearing (the actual). The three are deliberately separate: a coffer that has been
/// awarded but not opened is done for distribution and not done for the player.
/// </summary>
[Serializable]
public sealed class SlotNeed
{
    /// <summary>Target: where the player intends to get this slot from.</summary>
    public GearSource Source { get; set; } = GearSource.None;

    /// <summary>
    /// The piece has been handed over — won, bought with books, or already owned. This is what
    /// distribution goes by, and a gear scan may set it but must never clear it: not wearing
    /// something is no evidence of not owning it.
    /// </summary>
    public bool Obtained { get; set; }

    /// <summary>
    /// For <see cref="GearSource.TomeAugmented"/>: the upgrade material is in hand. The base
    /// tomestone piece is not tracked, since it costs no raid resource.
    /// </summary>
    public bool UpgradeObtained { get; set; }

    /// <summary>Item id from the imported gear set, 0 when the slot was set by hand.</summary>
    public uint BisItemId { get; set; }

    /// <summary>
    /// Materia melded into the target piece, as materia <b>item</b> ids — which is how both gear
    /// planners spell them.
    ///
    /// Only the target side is stored. What somebody currently has melded does not need to be:
    /// their finished attributes are read straight off the character and already contain it. This
    /// is for the set nobody is wearing yet, where there is nothing to measure.
    /// </summary>
    public List<uint> BisMateria { get; set; } = [];

    /// <summary>Actual: item id last seen equipped on the character. 0 when never seen.</summary>
    public uint EquippedItemId { get; set; }

    /// <summary>What the equipped item was classified as, worked out once when it was read.</summary>
    public GearSource EquippedSource { get; set; } = GearSource.None;

    /// <summary>Nothing left for the raid to provide for this slot.</summary>
    public bool IsSatisfied => Source switch
    {
        GearSource.Raid => Obtained,
        GearSource.TomeAugmented => UpgradeObtained,
        _ => true,
    };

    /// <summary>
    /// Whether the character is wearing what this slot was planned for. Prefers the exact item from
    /// the imported set and falls back to "something of the right kind", so a slot filled by hand
    /// still resolves.
    /// </summary>
    public bool IsWearingTarget =>
        EquippedItemId != 0 &&
        (BisItemId != 0 ? EquippedItemId == BisItemId : EquippedSource == Source);

    /// <summary>
    /// How the cell should read. <paramref name="scanned"/> says whether this character's gear has
    /// ever been looked at — without that, "not wearing it" cannot be told apart from "not known",
    /// and guessing would put a warning on every row of a fresh roster.
    /// </summary>
    public SlotState StateFor(bool scanned)
    {
        if (!Source.NeedsRaidResource())
            return SlotState.NotPlanned;

        if (!IsSatisfied)
            return SlotState.Needed;

        if (!scanned)
            return SlotState.Done;

        return IsWearingTarget ? SlotState.Done : SlotState.AssignedNotWorn;
    }

    public SlotNeed Clone() => new()
    {
        Source = Source,
        Obtained = Obtained,
        UpgradeObtained = UpgradeObtained,
        BisItemId = BisItemId,
        EquippedItemId = EquippedItemId,
        EquippedSource = EquippedSource,
    };
}

[Serializable]
public sealed class RosterMember
{
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;

    /// <summary><c>ClassJob</c> row id. Set from the party when the player is seen, editable by hand.</summary>
    public uint JobId { get; set; }

    /// <summary>XIVGear or Etro link the gear set was imported from, kept so it can be refreshed.</summary>
    public string GearPlannerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Why the last import was not applied, empty when it was. A set from a different tier
    /// classifies as crafted from top to bottom, which looks like a filled-in row and is worse
    /// than an empty one — so it is refused and said out loud instead.
    /// </summary>
    public string ImportWarning { get; set; } = string.Empty;

    public Dictionary<GearSlot, SlotNeed> Needs { get; set; } = new();

    /// <summary>Books held, keyed by encounter index (1..4).</summary>
    public Dictionary<int, int> Tokens { get; set; } = new();

    /// <summary>
    /// Upgrade materials held, keyed by side. Counted rather than flagged because one player can
    /// be sitting on two twines while another has none.
    /// </summary>
    public Dictionary<GearSide, int> Upgrades { get; set; } = new();

    /// <summary>Pieces won so far, used by the fairness term when two players score equally.</summary>
    public int ItemsReceived { get; set; }

    /// <summary>
    /// When this character's equipment was last read. Null means never, which is what separates
    /// "not wearing the piece" from "nobody has looked yet".
    /// </summary>
    public DateTime? LastScannedUtc { get; set; }

    /// <summary>Average item level as the game reported it at the last scan. 0 when never read.</summary>
    public int AverageItemLevel { get; set; }

    /// <summary>
    /// The character's finished attributes at the last scan, keyed by <c>BaseParam</c> row id.
    ///
    /// Stored rather than recomputed, because they cannot be recomputed: they are the totals the
    /// game itself worked out, materia and food included, and for anyone but the local player they
    /// only exist while the examine window is open. Kept as a plain id → value map so a stat the
    /// damage model does not use yet is still there when it does.
    /// </summary>
    public Dictionary<uint, int> Attributes { get; set; } = new();

    /// <summary>Job and level the attributes above were read on — not necessarily the roster's job.</summary>
    public uint MeasuredJobId { get; set; }

    public int MeasuredLevel { get; set; }

    /// <summary>Food the target set assumes, as an item id. 0 for none.</summary>
    public uint TargetFoodItemId { get; set; }

    public bool HasBeenScanned => LastScannedUtc != null;

    public bool HasMeasuredStats => Attributes.Count > 0 && MeasuredLevel > 0;

    public string Key => RosterKey.For(Name, World);

    public string DisplayName => string.IsNullOrWhiteSpace(World) ? Name : $"{Name} ({World})";

    public SlotNeed NeedFor(GearSlot slot)
    {
        if (!Needs.TryGetValue(slot, out var need))
            Needs[slot] = need = new SlotNeed();

        return need;
    }

    /// <summary>
    /// Lines the two ring slots up with the two ring targets, whichever way round they sit.
    ///
    /// Which finger a ring is on carries no information: the game reports the equipped pair in its
    /// own order, and a gear planner puts them in whichever slot the set was built in. Compared
    /// slot for slot, a player wearing both target rings the other way round reads as wearing
    /// <b>neither</b> — two misses, on a set that is finished. That in turn keeps two ring slots in
    /// the distribution and shows a gain for a piece already owned.
    ///
    /// Only the equipped ids ever move. The target set stays exactly as it was imported, because the
    /// order in that is what the plan and the sheets are written against.
    /// </summary>
    public bool AlignRings()
    {
        var first = NeedFor(GearSlot.Ring1);
        var second = NeedFor(GearSlot.Ring2);

        var straight = Matches(first.EquippedItemId, first.BisItemId) +
                       Matches(second.EquippedItemId, second.BisItemId);

        var crossed = Matches(first.EquippedItemId, second.BisItemId) +
                      Matches(second.EquippedItemId, first.BisItemId);

        // Only when crossing them is strictly better, so a pair that already lines up is left alone
        // and one that matches neither way round keeps the order the game reported.
        if (crossed <= straight)
            return false;

        (first.EquippedItemId, second.EquippedItemId) = (second.EquippedItemId, first.EquippedItemId);
        (first.EquippedSource, second.EquippedSource) = (second.EquippedSource, first.EquippedSource);

        return true;

        static int Matches(uint worn, uint target) => worn != 0 && worn == target ? 1 : 0;
    }

    public int TokensFor(int encounter) => Tokens.GetValueOrDefault(encounter);

    public int UpgradesFor(GearSide side) => Upgrades.GetValueOrDefault(side);
}
