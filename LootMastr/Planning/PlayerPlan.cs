using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Planning;

/// <summary>One thing a player still has to get out of the raid.</summary>
public readonly record struct OpenNeed(GearSlot Slot, int Encounter, bool IsUpgrade, GearSide Side)
{
    /// <summary>
    /// What this need is, named after the piece rather than the side. A player wanting three armour
    /// materials owes three different things, and calling them all "Left upgrade" makes one list
    /// look like a mistake in another.
    /// </summary>
    public string Describe() => IsUpgrade ? $"{Slots.CofferLabel(Slot)} upgrade" : Slots.CofferLabel(Slot);
}

/// <summary>
/// One tomestone piece a player still has to buy.
///
/// <paramref name="ForAugment"/> separates the two reasons a slot is on this list. A plain
/// tomestone piece only ever costs the player time; one that is going to be augmented is also what
/// decides whether a material dropping tonight can be used at all.
/// </summary>
public readonly record struct TomeNeed(GearSlot Slot, int Cost, bool ForAugment);

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

    /// <summary>Position in the roster, which is the player order the rules can be told to follow.</summary>
    public int Order { get; init; }

    public int ItemsReceived { get; set; }

    public List<OpenNeed> Open { get; init; } = new();

    /// <summary>Books held, indexed 1..4. Index 0 is unused so the fight number reads directly.</summary>
    public int[] Tokens { get; init; } = new int[MaxEncounters + 1];

    /// <summary>Tomestones in hand, starting from whatever the weeks before the tier banked.</summary>
    public int Tomes { get; set; }

    /// <summary>Tomestone pieces still to buy. Empty for a player whose set needs none.</summary>
    public List<TomeNeed> TomeOpen { get; init; } = new();

    /// <summary>
    /// What each kind of drop would be worth to this player, in flat damage per second.
    ///
    /// Worked out once before a projection starts and then held constant, which is an approximation
    /// worth naming: taking the body piece does slightly change what the legs would be worth, through
    /// the stat totals. The effect is second order and the alternative is re-running a damage model
    /// inside the simulator's inner loop — so it is measured once, and the ranking it produces is
    /// stable for it.
    ///
    /// Empty is the normal case. A group that has not read anyone's gear has no gains, and the rules
    /// fall back to counting items.
    /// </summary>
    public Dictionary<GearSlot, double> SlotGains { get; init; } = new();

    public Dictionary<GearSide, double> UpgradeGains { get; init; } = new();

    /// <summary>Week the player ran out of open needs, or -1 while still short of something.</summary>
    public int FinishedWeek { get; set; } = -1;

    /// <summary>
    /// Week the last tomestone piece was bought, or -1 while still saving.
    ///
    /// Kept apart from <see cref="FinishedWeek"/> because the two answer different questions and a
    /// group needs both: what the raid still owes them, and when their set is actually finished.
    /// The second is never earlier than the first and is often several weeks later.
    /// </summary>
    public int TomeFinishedWeek { get; set; } = -1;

    public bool IsDone => Open.Count == 0;

    public bool IsTomeDone => TomeOpen.Count == 0;

    /// <summary>Nothing left from either shop.</summary>
    public bool IsFullyDone => IsDone && IsTomeDone;

    public PlayerPlan Clone() => new()
    {
        Key = Key,
        Name = Name,
        Role = Role,
        Order = Order,
        ItemsReceived = ItemsReceived,
        Open = [..Open],
        Tokens = (int[])Tokens.Clone(),
        Tomes = Tomes,

        // Copied rather than shared: a run buys things out of it.
        TomeOpen = [..TomeOpen],
        FinishedWeek = FinishedWeek,
        TomeFinishedWeek = TomeFinishedWeek,

        // Shared, not copied: they are worked out before a run and never written during one.
        SlotGains = SlotGains,
        UpgradeGains = UpgradeGains,
    };

    /// <summary>What a coffer for this slot would be worth, in damage per second. 0 when not known.</summary>
    public double GainFor(GearSlot slot) => SlotGains.GetValueOrDefault(Slots.CofferSlot(slot));

    /// <summary>What a material for this side would be worth, on the best piece it could go into.</summary>
    public double GainForUpgrade(GearSide side) => UpgradeGains.GetValueOrDefault(side);

    /// <summary>
    /// Reads a roster member's need list against a tier. Slots whose source costs the raid nothing
    /// never appear, so a player whose whole set is crafted is simply done.
    /// </summary>
    public static PlayerPlan From(RosterMember member, RaidRole role, TierDefinition tier, int order = 0)
    {
        var plan = new PlayerPlan
        {
            Key = member.Key,
            Name = member.Name,
            Role = role,
            Order = order,
            ItemsReceived = member.ItemsReceived,
        };

        for (var encounter = 1; encounter <= MaxEncounters; encounter++)
            plan.Tokens[encounter] = member.TokensFor(encounter);

        plan.Tomes = TomeLedger.StartingBalance(tier);

        var raidRingTaken = false;

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            // Both tomestone sources cost tomestones, and neither is a raid resource — so this runs
            // before the raid check rather than inside it. An augmented piece is on both lists: the
            // material comes out of a chest, the thing it goes into comes out of a vendor.
            if (need.Source is GearSource.Tome or GearSource.TomeAugmented && !need.BaseObtained &&
                tier.TomeCostForSlot(slot) is { } price and > 0)
            {
                plan.TomeOpen.Add(new TomeNeed(slot, price, need.Source == GearSource.TomeAugmented));
            }

            if (!need.Source.NeedsRaidResource() || need.IsSatisfied)
                continue;

            if (need.Source == GearSource.Raid)
            {
                // Raid gear is unique, so a character can only ever wear one raid ring. A set
                // claiming two is a mistake in the set, and counting both would have the planner
                // chase a coffer that could not be equipped even if it won it.
                if (Slots.IsRing(slot))
                {
                    if (raidRingTaken)
                        continue;

                    raidRingTaken = true;
                }

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

        // The tomestone weapon is nobody's target set — it is a stopgap somebody takes because the
        // raid weapon is weeks away. So it never appears as a need, and the 500 it costs would go
        // unnoticed if the stone were not recorded. It is over a week of income and it delays every
        // other piece that player was saving for, which is exactly what makes "who needs the fewest
        // tomestones" the right way to choose who takes the stone in the first place.
        if (member.WeaponTokenObtained && !plan.TomeOpen.Any(n => Slots.CofferSlot(n.Slot) == GearSlot.Weapon) &&
            tier.TomeCostForSlot(GearSlot.Weapon) is { } weapon and > 0)
        {
            plan.TomeOpen.Add(new TomeNeed(GearSlot.Weapon, weapon, false));
        }

        return plan;
    }

    /// <summary>
    /// Whether a material for this side could be put to use, rather than held.
    ///
    /// The question the group actually asks when a twine drops: can this person augment anything
    /// with it? They can if the piece under it is already bought, or if they can buy it now. If it
    /// is four weeks of saving away, the material sits in their bag while somebody else could have
    /// worn it the same evening.
    ///
    /// Only ever a preference, never a veto — see <c>WeekSimulator.AwardUpgrade</c>. A material
    /// nobody can use yet still goes to somebody, because holding it beats leaving it in the chest.
    /// </summary>
    public bool CanUseUpgrade(GearSide side)
    {
        foreach (var need in Open)
        {
            if (!need.IsUpgrade || need.Side != side)
                continue;

            var index = TomeOpen.FindIndex(t => t.Slot == need.Slot);

            // Not on the list means it is already bought, which is the strongest form of yes.
            if (index < 0 || TomeLedger.CanAfford(this, TomeOpen[index].Cost))
                return true;
        }

        return false;
    }

    /// <summary>Buys one tomestone piece off the list. False when it was not on it.</summary>
    public bool TakeTome(GearSlot slot)
    {
        var index = TomeOpen.FindIndex(n => n.Slot == slot);
        if (index < 0)
            return false;

        TomeOpen.RemoveAt(index);
        return true;
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

    public bool TakeUpgrade(GearSide side) => TakeUpgrade(side, out _);

    /// <summary>
    /// Drops the first open need for a material of this side, and says which piece it was for.
    ///
    /// Which piece matters to whoever reads the plan. A player with augmented head, body and legs
    /// owes <b>three</b> armour materials, and a schedule calling all three "Left upgrade" shows the
    /// same line two and three times over — which reads as the plan double-counting rather than as
    /// what it is.
    /// </summary>
    public bool TakeUpgrade(GearSide side, out GearSlot slot)
    {
        slot = default;

        var index = Open.FindIndex(n => n.IsUpgrade && n.Side == side);
        if (index < 0)
            return false;

        slot = Open[index].Slot;

        Open.RemoveAt(index);
        ItemsReceived++;
        return true;
    }
}
