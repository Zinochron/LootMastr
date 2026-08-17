using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>What the rule needs to know about one player in the running for one drop.</summary>
public readonly record struct Contender(string Key, RaidRole Role, int Order, int ItemsReceived, int OpenNeeds);

/// <summary>Where a contender came out, and the two positions that put them there.</summary>
public readonly record struct Placing(Contender Who, int Queue, int Spread, double Score);

/// <summary>One name in a ranking, with why they are where they are. Text only, so the forecast can
/// carry its own reasoning without dragging the roster into it.</summary>
public readonly record struct AwardCandidate(string Name, string Reason);

/// <summary>
/// The one rule that decides who a drop goes to.
///
/// There used to be two. The loot window ranked candidates by running a full simulation for each of
/// them; the week projection used a quick "whoever has most left" inside the simulator. They
/// genuinely disagreed, which is why the same drop could name one player in the plan and another in
/// the chest — and neither answer could be explained in a sentence. Both now come through here, so
/// the tables agree because they are the same arithmetic, not because they were reconciled.
///
/// It is deliberately not an optimiser. A group's loot policy is a decision, not a search result:
/// "damage first, then in roster order, and how much of it to share out" is what statics actually
/// argue about, and all three of those are settings rather than something inferred from a
/// projection. The simulation is still run — it is what says when everyone is finished — but it no
/// longer decides anything.
/// </summary>
public static class DropOrder
{
    /// <summary>Ranks the candidates for one drop, best first.</summary>
    public static List<Placing> Rank(PriorityRules rules, IReadOnlyList<Contender> candidates)
    {
        if (candidates.Count == 0)
            return [];

        // The order the group declared: role first, then the roster. Where a switch is off, that
        // half simply drops out and the one below carries the sort.
        var queue = candidates
                    .OrderBy(c => rules.UseRoleOrder ? rules.RankOf(c.Role) : 0)
                    .ThenBy(c => rules.UsePlayerOrder ? c.Order : 0)
                    .ThenBy(c => c.ItemsReceived)
                    .ThenByDescending(c => c.OpenNeeds)
                    .ThenBy(c => c.Order)
                    .Select(c => c.Key)
                    .ToList();

        // The other end: whoever has been served least, and failing that has most left to get.
        var spread = candidates
                     .OrderBy(c => c.ItemsReceived)
                     .ThenByDescending(c => c.OpenNeeds)
                     .ThenBy(c => rules.UseRoleOrder ? rules.RankOf(c.Role) : 0)
                     .ThenBy(c => c.Order)
                     .Select(c => c.Key)
                     .ToList();

        var share = Math.Clamp(rules.Spread, 0d, 1d);
        var mostOpen = candidates.Max(c => c.OpenNeeds);

        var placings = candidates.Select(c =>
        {
            var q = queue.IndexOf(c.Key);
            var s = spread.IndexOf(c.Key);

            return new Placing(c, q, s, ((1 - share) * q) + (share * Served(c, mostOpen)));
        }).ToList();

        // Role, when it is on, is a gate rather than a term: a healer waits while a tank still wants
        // the same piece, whatever the slider says. That is what a group means by gearing damage
        // first, and a slider that could quietly overturn it would not be the setting they asked for.
        return placings
               .OrderBy(p => rules.UseRoleOrder ? rules.RankOf(p.Who.Role) : 0)
               .ThenBy(p => p.Score)
               .ThenBy(p => p.Queue)
               .ThenBy(p => p.Who.Order)
               .ThenBy(p => p.Who.Key, StringComparer.Ordinal)
               .ToList();
    }

    /// <summary>
    /// How well served a candidate already is, on an absolute scale: **one item already won counts
    /// as one place in the order**. That calibration is the whole of the slider's behaviour.
    ///
    /// It used to be a position on a sorted list, and that made the slider binary. Both axes were
    /// ranks, so with two candidates the scores were always <c>s</c> against <c>1 − s</c> and the
    /// order flipped at exactly 0.5 — however far apart the two actually were. Normalising the ranks
    /// would not have helped; the extremes are 0 and 1 by construction either way. Only an absolute
    /// quantity moves the tipping point: someone first in the order who has already won three items
    /// is passed at a spread of 0.25, someone who has won one is passed at 0.5, and someone who has
    /// won nothing is never passed at all.
    ///
    /// Open needs break ties inside a whole item — a player level with another on items won, but
    /// with more still to get, is the needier of the two, and never by enough to outweigh a win.
    /// </summary>
    private static double Served(Contender candidate, int mostOpen)
    {
        var tiebreak = mostOpen <= 0 ? 0d : (double)candidate.OpenNeeds / (mostOpen + 1);
        return candidate.ItemsReceived - tiebreak;
    }

    /// <summary>
    /// Why a candidate is where they are, in the terms the settings are written in. Only the parts
    /// that are switched on are mentioned — a reason naming a rule the group turned off is worse
    /// than no reason at all.
    /// </summary>
    public static string Explain(PriorityRules rules, Placing placing)
    {
        var parts = new List<string>(4);

        if (rules.UseRoleOrder)
            parts.Add(Describe(placing.Who.Role));

        if (rules.UsePlayerOrder)
            parts.Add($"#{placing.Who.Order + 1} in the player order");

        parts.Add(placing.Who.ItemsReceived == 0 ? "nothing won yet" : $"{placing.Who.ItemsReceived} won so far");
        parts.Add(placing.Who.OpenNeeds == 1 ? "1 piece left" : $"{placing.Who.OpenNeeds} pieces left");

        return string.Join("; ", parts);
    }

    private static string Describe(RaidRole role) =>
        role == RaidRole.Dps ? "damage dealer" : role.ToString().ToLowerInvariant();
}
