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

        var placings = candidates.Select(c =>
        {
            var q = queue.IndexOf(c.Key);
            var s = spread.IndexOf(c.Key);

            // Two positions in the same units, mixed by the one slider. At 0 the top of the order
            // takes everything it can use; at 1 the drop goes to whoever is furthest behind.
            return new Placing(c, q, s, ((1 - share) * q) + (share * s));
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
