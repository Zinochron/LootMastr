using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Planning;

/// <summary>One thing a player still has to get out of the raid.</summary>
public readonly record struct OpenNeed(GearSlot Slot, int Encounter, bool IsUpgrade, GearSide Side)
{
    /// <summary>What the drop or the book has to be, for matching against what is on offer.</summary>
    public string Describe() => IsUpgrade ? $"{Side} upgrade" : Slots.Label(Slot);
}

/// <summary>
/// A player as the simulator sees them: what is left, what books they hold, nothing else. Kept
/// separate from <see cref="RosterMember"/> so a simulation can be run on throwaway copies.
/// </summary>
public sealed class PlayerPlan
{
    public const int MaxEncounters = 4;

    public required string Key { get; init; }
    public required string Name { get; init; }
    public required RaidRole Role { get; init; }

    public int ItemsReceived { get; set; }

    public List<OpenNeed> Open { get; init; } = new();

    /// <summary>Books held, indexed 1..4. Index 0 is unused so the fight number reads directly.</summary>
    public int[] Tokens { get; init; } = new int[MaxEncounters + 1];

    /// <summary>Week the player ran out of open needs, or -1 while still short of something.</summary>
    public int FinishedWeek { get; set; } = -1;

    public bool IsDone => Open.Count == 0;

    public PlayerPlan Clone() => new()
    {
        Key = Key,
        Name = Name,
        Role = Role,
        ItemsReceived = ItemsReceived,
        Open = [..Open],
        Tokens = (int[])Tokens.Clone(),
        FinishedWeek = FinishedWeek,
    };

    /// <summary>
    /// Reads a roster member's need list against a tier. Slots whose source costs the raid nothing
    /// never appear, so a player whose whole set is crafted is simply done.
    /// </summary>
    public static PlayerPlan From(RosterMember member, RaidRole role, TierDefinition tier)
    {
        var plan = new PlayerPlan
        {
            Key = member.Key,
            Name = member.Name,
            Role = role,
            ItemsReceived = member.ItemsReceived,
        };

        for (var encounter = 1; encounter <= MaxEncounters; encounter++)
            plan.Tokens[encounter] = member.TokensFor(encounter);

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);
            if (!need.Source.NeedsRaidResource() || need.IsSatisfied)
                continue;

            if (need.Source == GearSource.Raid)
            {
                // A shield drops from no fight at all and is bought with weapon books, so the
                // encounter a need belongs to falls back to whichever book pays for it. Without
                // this the slot would quietly vanish from the plan instead of showing up as work.
                var encounter = tier.EncounterForSlot(slot)?.Index ?? tier.CostForSlot(slot)?.Encounter;
                if (encounter != null)
                    plan.Open.Add(new OpenNeed(slot, encounter.Value, false, Slots.SideOf(slot)));

                continue;
            }

            var side = Slots.SideOf(slot);
            var upgradeFight = tier.EncounterForUpgrade(side)?.Index ?? tier.CostForUpgrade(side)?.Encounter;
            if (upgradeFight != null)
                plan.Open.Add(new OpenNeed(slot, upgradeFight.Value, true, side));
        }

        return plan;
    }

    public bool Wants(GearSlot slot) =>
        Open.Any(n => !n.IsUpgrade && Slots.CofferSlot(n.Slot) == Slots.CofferSlot(slot));

    public bool WantsUpgrade(GearSide side) => Open.Any(n => n.IsUpgrade && n.Side == side);

    /// <summary>Drops the first open need matching a coffer for this slot. False when nothing matched.</summary>
    public bool TakeSlot(GearSlot slot)
    {
        var index = Open.FindIndex(n => !n.IsUpgrade && Slots.CofferSlot(n.Slot) == Slots.CofferSlot(slot));
        if (index < 0)
            return false;

        Open.RemoveAt(index);
        ItemsReceived++;
        return true;
    }

    public bool TakeUpgrade(GearSide side)
    {
        var index = Open.FindIndex(n => n.IsUpgrade && n.Side == side);
        if (index < 0)
            return false;

        Open.RemoveAt(index);
        ItemsReceived++;
        return true;
    }
}
