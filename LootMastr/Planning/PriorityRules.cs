using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>
/// The group's loot policy, in three settings.
///
/// It used to be five weights feeding a simulation, and the trouble with that was not that the
/// numbers were wrong — it was that nobody could look at a plan and say why it had chosen someone.
/// A weight on a projected finish week is a preference the arithmetic can outvote, so a healer
/// saving four weeks beat a damage dealer saving one, and turning that off meant guessing at a
/// slider. What a static actually decides is much smaller: which roles come first, which players
/// come first, and how much of the loot to share out rather than funnel.
///
/// So: <see cref="RoleOrder"/> with <see cref="UseRoleOrder"/>, the roster's own order with
/// <see cref="UsePlayerOrder"/>, and <see cref="Spread"/> between the two extremes. Everything the
/// plan shows follows from those, through <see cref="DropOrder"/>.
/// </summary>
/// <summary>Which quantity the sharing-out end of the slider measures.</summary>
public enum NeedBasis
{
    /// <summary>Who has won least. A rule about people, and the one that needs no gear read.</summary>
    MissingGear,

    /// <summary>Who the piece is worth most to. Needs everyone's gear read to say anything.</summary>
    DpsGain,

    /// <summary>Both, on one scale: a point of damage percent and an item already won weigh the same.</summary>
    Both,
}

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
    /// Whether that order applies at all. On, it is a gate: a healer waits while a tank still wants
    /// the same piece, whatever the rest of the rule would prefer. Off, role is ignored entirely and
    /// the player order and the spread decide on their own.
    /// </summary>
    public bool UseRoleOrder { get; set; } = true;

    /// <summary>
    /// Whether the roster's own order counts. On, being higher up the list is a reason to be served
    /// first — how strong a reason is <see cref="Spread"/>. Off, everyone inside a role is equal
    /// until one of them has won more than another.
    /// </summary>
    public bool UsePlayerOrder { get; set; } = true;

    /// <summary>
    /// How much to share the loot out, from 0 to 1.
    ///
    /// At <b>0</b> the top of the order takes everything it can use — one main damage dealer geared
    /// as fast as the raid allows. At <b>1</b> every drop goes to whoever is furthest behind,
    /// regardless of where they sit in the list. In between, position and neediness are mixed: a
    /// player near the top wins unless someone below them is a long way further back.
    ///
    /// The role order is not part of this. It is a gate above it, so sliding all the way to 1 shares
    /// the loot out <i>within</i> a role rather than handing it past one.
    /// </summary>
    public double Spread { get; set; } = 0.5;

    /// <summary>
    /// What "needs it more" means on the sharing-out side of the slider.
    ///
    /// Sharing out has always meant one thing — whoever has won least — and that is a fair rule
    /// about people. Expert mode can answer a different question: which of them the piece is worth
    /// most to. Both are defensible and a group should be able to say which it wants.
    /// </summary>
    public NeedBasis Basis { get; set; } = NeedBasis.MissingGear;

    /// <summary>
    /// Whether an alt may take a piece no main character wants.
    ///
    /// Off is the honest default: the point of a second character is to clear a fight again, and a
    /// coffer that goes on one is a coffer that left the raid. On, the alt is a last resort rather
    /// than a competitor — never ahead of a main, only after every one of them has passed.
    /// </summary>
    public bool AltsMayTakeSpareGear { get; set; }

    /// <summary>Where a role sits in the order. Lower goes first; anything unknown goes last.</summary>
    public int RankOf(RaidRole role)
    {
        var index = RoleOrder.IndexOf(role);
        return index < 0 ? RoleOrder.Count : index;
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
