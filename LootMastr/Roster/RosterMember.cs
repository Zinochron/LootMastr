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

/// <summary>What one player wants in one slot, and whether they already have it.</summary>
[Serializable]
public sealed class SlotNeed
{
    public GearSource Source { get; set; } = GearSource.None;

    /// <summary>The piece itself is done — won, bought with books, or already owned.</summary>
    public bool Obtained { get; set; }

    /// <summary>
    /// For <see cref="GearSource.TomeAugmented"/>: the upgrade material is in hand. The base
    /// tomestone piece is not tracked, since it costs no raid resource.
    /// </summary>
    public bool UpgradeObtained { get; set; }

    /// <summary>Item id from the imported gear set, 0 when the slot was set by hand.</summary>
    public uint BisItemId { get; set; }

    /// <summary>Nothing left for the raid to provide for this slot.</summary>
    public bool IsSatisfied => Source switch
    {
        GearSource.Raid => Obtained,
        GearSource.TomeAugmented => UpgradeObtained,
        _ => true,
    };

    public SlotNeed Clone() => new()
    {
        Source = Source,
        Obtained = Obtained,
        UpgradeObtained = UpgradeObtained,
        BisItemId = BisItemId,
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

    public string Key => RosterKey.For(Name, World);

    public string DisplayName => string.IsNullOrWhiteSpace(World) ? Name : $"{Name} ({World})";

    public SlotNeed NeedFor(GearSlot slot)
    {
        if (!Needs.TryGetValue(slot, out var need))
            Needs[slot] = need = new SlotNeed();

        return need;
    }

    public int TokensFor(int encounter) => Tokens.GetValueOrDefault(encounter);

    public int UpgradesFor(GearSide side) => Upgrades.GetValueOrDefault(side);
}
