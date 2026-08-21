using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;

namespace LootMastr.Roster;

/// <summary>How the plugin came to know somebody received something.</summary>
public enum LootSource
{
    /// <summary>An obtain line in chat. The only witness that is never wrong about the item.</summary>
    Chat,

    /// <summary>Somebody pressed Record in the loot tab.</summary>
    ByHand,
}

/// <summary>
/// One thing that changed hands, and when.
///
/// The plugin has always heard these — <c>ObtainTracker</c> watches chat for them — and has always
/// thrown them away after forty entries. What is not written down tonight is gone next week, and
/// "who got the second twine" is a question groups actually ask.
/// </summary>
[Serializable]
public sealed class LootEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime AtUtc { get; set; }

    public string PlayerKey { get; set; } = string.Empty;

    public string PlayerName { get; set; } = string.Empty;

    public uint ItemId { get; set; }

    /// <summary>
    /// What it was called, written down rather than looked up later.
    ///
    /// A history read on somebody else's client, or after the group moves to the next tier, still
    /// has to say what the thing was. Resolving an id at display time works right up until the
    /// moment it does not, and then the record is a number.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    public GearSlot? Slot { get; set; }

    public GearSide? Upgrade { get; set; }

    public SpecialDrop? Special { get; set; }

    /// <summary>Which fight, when the zone said so. 0 when it did not.</summary>
    public int Encounter { get; set; }

    public LootSource How { get; set; }

    /// <summary>What to call it in a list, preferring the game's own name.</summary>
    public string What =>
        !string.IsNullOrEmpty(ItemName) ? ItemName
        : Upgrade != null ? $"{Upgrade} upgrade"
        : Slot?.CofferLabel() ?? Special?.ToString() ?? "something";

    /// <summary>
    /// Whether two records are plainly the same event seen twice.
    ///
    /// Every client in the party hears the same chat line, so the same handover arrives from several
    /// machines with different ids. Same player, same item, close enough in time is the strongest
    /// statement that can be made without the game giving events an identity of their own.
    /// </summary>
    public bool LooksLike(LootEvent other, TimeSpan tolerance) =>
        PlayerKey == other.PlayerKey && ItemId == other.ItemId &&
        (AtUtc - other.AtUtc).Duration() <= tolerance;
}

/// <summary>
/// The group's record of what was handed out, and the one place two copies of it are reconciled.
///
/// <b>This is the only part of the synced document that merges rather than being replaced.</b>
/// Everywhere else last-write-wins is right, because everyone is looking at roughly the same state
/// and the loser of a race can simply look again. A log is different: two clients in one party each
/// hear the same evening and each write down half of it, and whoever pushes second would erase the
/// other's half for good.
///
/// It is a deliberate exception and should stay one. Anything else that wants merging has to make
/// the same argument rather than pointing at this.
/// </summary>
public static class LootHistory
{
    /// <summary>How far apart two sightings of one handover may be and still be one handover.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much is kept. A tier is a few hundred rows; this is room to be wrong by a factor of ten.
    ///
    /// It matters because the history rides inside the synced document, and a document that grows
    /// without bound eventually stops being pushed at all.
    /// </summary>
    public const int Limit = 2000;

    /// <summary>
    /// Both copies, with the same event counted once, newest first.
    ///
    /// Ids settle the easy case — the same row travelling back from the server. The time window
    /// settles the hard one, where two clients wrote independent records of one moment.
    /// </summary>
    public static List<LootEvent> Merge(IEnumerable<LootEvent>? mine, IEnumerable<LootEvent>? theirs,
                                        IEnumerable<string>? forgotten = null)
    {
        var result = new List<LootEvent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var gone = forgotten == null ? null : new HashSet<string>(forgotten, StringComparer.Ordinal);

        foreach (var entry in (mine ?? []).Concat(theirs ?? []).OrderByDescending(e => e.AtUtc))
        {
            if (entry == null)
                continue;

            if (!string.IsNullOrEmpty(entry.Id) && !ids.Add(entry.Id))
                continue;

            // Deleting a row out of a merged log does not work without this. Union means the next
            // pull brings back whatever anybody else still holds, so a deletion has to be a fact
            // that travels — the absence of a row says nothing.
            if (gone != null && !string.IsNullOrEmpty(entry.Id) && gone.Contains(entry.Id))
                continue;

            // Linear over the window rather than over everything: the list is in time order, so the
            // only candidates are the handful of rows within the tolerance of this one.
            var duplicate = false;

            for (var i = result.Count - 1; i >= 0; i--)
            {
                if ((result[i].AtUtc - entry.AtUtc).Duration() > Tolerance)
                    break;

                if (!result[i].LooksLike(entry, Tolerance))
                    continue;

                duplicate = true;
                break;
            }

            if (!duplicate)
                result.Add(entry);
        }

        return result.Count > Limit ? result.Take(Limit).ToList() : result;
    }

    /// <summary>
    /// The ids both sides have deleted.
    ///
    /// Unioned like the history itself and for the same reason: a client that never saw the
    /// deletion would otherwise push the row straight back on its next turn.
    /// </summary>
    public static List<string> MergeForgotten(IEnumerable<string>? mine, IEnumerable<string>? theirs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // Newest last, so trimming the front drops the oldest tombstones first — those are the ones
        // whose rows no client is still carrying.
        foreach (var id in (mine ?? []).Concat(theirs ?? []))
        {
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
                result.Add(id);
        }

        return result.Count > Limit ? result.Skip(result.Count - Limit).ToList() : result;
    }

    /// <summary>Deletes one row, and records that it was deleted so a sync cannot undo it.</summary>
    public static void Forget(List<LootEvent> history, List<string> forgotten, LootEvent entry)
    {
        history.RemoveAll(e => e.Id == entry.Id);

        if (!string.IsNullOrEmpty(entry.Id) && !forgotten.Contains(entry.Id))
            forgotten.Add(entry.Id);

        Trim(forgotten);
    }

    /// <summary>Deletes everything currently held, tombstoning each row on the way out.</summary>
    public static void ForgetAll(List<LootEvent> history, List<string> forgotten)
    {
        foreach (var entry in history)
        {
            if (!string.IsNullOrEmpty(entry.Id) && !forgotten.Contains(entry.Id))
                forgotten.Add(entry.Id);
        }

        history.Clear();
        Trim(forgotten);
    }

    private static void Trim(List<string> forgotten)
    {
        if (forgotten.Count > Limit)
            forgotten.RemoveRange(0, forgotten.Count - Limit);
    }

    /// <summary>Adds one event to a list that is kept newest first.</summary>
    public static void Add(List<LootEvent> history, LootEvent entry)
    {
        foreach (var existing in history)
        {
            if (existing.LooksLike(entry, Tolerance))
                return;
        }

        history.Insert(0, entry);

        if (history.Count > Limit)
            history.RemoveRange(Limit, history.Count - Limit);
    }
}
