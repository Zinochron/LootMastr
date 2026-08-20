using System.Collections.Generic;
using System.Linq;

namespace LootMastr.Data;

/// <summary>
/// Raid roles as the distribution cares about them. The game splits damage into melee, physical
/// ranged and caster; loot priority does not, so they collapse into one.
///
/// Kept in its own file with no game dependencies so the planner can be compiled on its own.
/// </summary>
public enum RaidRole
{
    Unknown,
    Tank,
    Healer,
    Dps,
}

/// <summary>
/// A list of roles somebody has put in an order, and the two operations any such list needs.
///
/// There are two of these now — the order gear is handed out in, and the mount's own — and they are
/// edited by the same pair of arrows on screen. One implementation rather than two, because the
/// second copy is where "anything unknown goes last" quietly stops being true.
/// </summary>
public static class Roles
{
    /// <summary>Every role loot is actually distributed by. <c>Unknown</c> is not one.</summary>
    public static readonly RaidRole[] All = [RaidRole.Dps, RaidRole.Tank, RaidRole.Healer];

    /// <summary>Moves one role by <paramref name="delta"/> places, or does nothing at either end.</summary>
    public static void Move(List<RaidRole> order, RaidRole role, int delta)
    {
        var index = order.IndexOf(role);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= order.Count)
            return;

        order.RemoveAt(index);
        order.Insert(target, role);
    }

    /// <summary>
    /// Fills in any role the stored order is missing and drops anything that is not a role.
    ///
    /// Runs before the list is drawn or read. A config written by an older version, or one a person
    /// edited, is missing an entry rather than corrupt — and a missing entry would otherwise rank
    /// last silently, which looks exactly like a decision somebody made.
    /// </summary>
    public static List<RaidRole> Complete(List<RaidRole> order)
    {
        var result = order.Where(r => r != RaidRole.Unknown).Distinct().ToList();

        foreach (var role in All)
        {
            if (!result.Contains(role))
                result.Add(role);
        }

        return result;
    }

    /// <summary>Where a role sits. Lower goes first; anything missing goes last.</summary>
    public static int RankIn(List<RaidRole> order, RaidRole role)
    {
        var index = order.IndexOf(role);
        return index < 0 ? order.Count : index;
    }
}
