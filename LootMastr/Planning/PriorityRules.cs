using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>
/// How the ranking decides between people, beyond what the simulation says.
///
/// Role is an <b>order</b>, not a weight. Weights were the first attempt and they do not express
/// what a static actually does: a multiplier on someone's finish week is a preference that the
/// arithmetic can outvote, so a healer saving four weeks beat a damage dealer saving one. Groups do
/// not run that way — they gear damage first, then tanks, then healers, and mean it.
/// </summary>
[Serializable]
public sealed class PriorityRules
{
    /// <summary>
    /// Roles in the order they get geared. Damage first is the convention; the list is editable
    /// because the exception is a group that has decided otherwise, not a group that disagrees
    /// about what the words mean.
    /// </summary>
    public List<RaidRole> RoleOrder { get; set; } = [RaidRole.Dps, RaidRole.Tank, RaidRole.Healer];

    /// <summary>
    /// Whether that order decides outright. On, a healer waits while any tank still wants the same
    /// piece — which is what "we gear damage first" means when a group says it. Off, role becomes a
    /// nudge inside the arithmetic again, and the simulation can outvote it.
    /// </summary>
    public bool StrictRoleOrder { get; set; } = true;

    /// <summary>
    /// How much one step down the order is worth when it is <em>not</em> strict. Only read then.
    /// </summary>
    public double RoleStep { get; set; } = 0.5;

    /// <summary>
    /// Weeks of simulated delay one already-won item is worth. Purely a tiebreak between players
    /// the rest of the ranking could not separate.
    /// </summary>
    public double FairnessWeight { get; set; } = 0.05;

    /// <summary>
    /// Weight on the group's last finisher against the weighted average. At 1 the plan optimises
    /// for everyone being done; at 0 it only cares about the average, which is a different thing
    /// and worth knowing you have asked for.
    /// </summary>
    public double LastFinisherWeight { get; set; } = 1.0;

    /// <summary>Where a role sits in the order. Lower goes first; anything unknown goes last.</summary>
    public int RankOf(RaidRole role)
    {
        var index = RoleOrder.IndexOf(role);
        return index < 0 ? RoleOrder.Count : index;
    }

    /// <summary>
    /// The soft form of the same order, for the weighted average. Derived from the position rather
    /// than stored separately so the two can never say different things.
    /// </summary>
    public double WeightFor(RaidRole role)
    {
        var last = Math.Max(1, RoleOrder.Count - 1);
        return 1.0 + ((last - Math.Min(RankOf(role), last)) * Math.Max(0, RoleStep));
    }

    public void Move(RaidRole role, int delta)
    {
        var index = RoleOrder.IndexOf(role);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= RoleOrder.Count)
            return;

        RoleOrder.RemoveAt(index);
        RoleOrder.Insert(target, role);
    }

    /// <summary>Fills in any role the stored order is missing, so an old config still works.</summary>
    public void EnsureComplete()
    {
        foreach (var role in new[] { RaidRole.Dps, RaidRole.Tank, RaidRole.Healer })
        {
            if (!RoleOrder.Contains(role))
                RoleOrder.Add(role);
        }

        RoleOrder = RoleOrder.Where(r => r != RaidRole.Unknown).Distinct().ToList();
    }
}
