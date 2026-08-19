using System;
using System.Collections.Generic;
using System.Linq;

namespace LootMastr.Planning;

/// <summary>
/// One recurring evening the group raids on.
///
/// Stored in <b>UTC</b> and shown in local time. A group spread over two countries otherwise has two
/// answers to "when do we start", and doing the conversion once at the edge is cheaper than
/// forgetting it somewhere in the middle.
/// </summary>
[Serializable]
public sealed class RaidSlot
{
    public DayOfWeek Day { get; set; } = DayOfWeek.Tuesday;

    /// <summary>Minutes since midnight, UTC.</summary>
    public int StartMinutesUtc { get; set; } = 19 * 60;

    public int DurationMinutes { get; set; } = 180;

    public RaidSlot Clone() => (RaidSlot)MemberwiseClone();
}

/// <summary>
/// Turns a weekly pattern into actual moments, and a week number into actual days.
///
/// Pure arithmetic over <see cref="DateTime"/> in UTC, so the harness can assert on it without a
/// clock, a game or a timezone. Every method takes "now" rather than reading it — a calendar that
/// consults the system clock is a calendar nobody can test at midnight on a Tuesday.
/// </summary>
public static class RaidCalendar
{
    public const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// When the lockout most recently rolled over, at or before <paramref name="nowUtc"/>.
    ///
    /// The reset is the one clock in this game that decides what a "week" is: books, drops and the
    /// tomestone cap all turn over together.
    /// </summary>
    public static DateTime LastReset(DayOfWeek resetDay, int resetMinutesUtc, DateTime nowUtc)
    {
        var minutes = Wrap(resetMinutesUtc);
        var candidate = nowUtc.Date.AddMinutes(minutes);

        // Step back to the reset day, then one more week if that lands in the future — which it does
        // whenever today *is* reset day and the hour has not come round yet.
        var back = ((int)nowUtc.DayOfWeek - (int)resetDay + 7) % 7;
        candidate = candidate.AddDays(-back);

        return candidate > nowUtc ? candidate.AddDays(-7) : candidate;
    }

    public static DateTime NextReset(DayOfWeek resetDay, int resetMinutesUtc, DateTime nowUtc) =>
        LastReset(resetDay, resetMinutesUtc, nowUtc).AddDays(7);

    /// <summary>
    /// The days one planned week covers, week 1 being the lockout that is running now.
    ///
    /// Deliberately the current lockout rather than the next one. <c>LootPlanner.ComingWeek</c> is
    /// about the clears the group has not done yet and the books already in their pockets — that is
    /// this week, not the one after the reset, and a label has to agree with the number beside it.
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) WeekWindow(
        DayOfWeek resetDay, int resetMinutesUtc, int week, DateTime nowUtc)
    {
        var start = LastReset(resetDay, resetMinutesUtc, nowUtc).AddDays(7 * (Math.Max(1, week) - 1));

        return (start, start.AddDays(7));
    }

    /// <summary>Which planned week a moment falls in, counting the running lockout as one.</summary>
    public static int WeekOf(DayOfWeek resetDay, int resetMinutesUtc, DateTime atUtc, DateTime nowUtc)
    {
        var thisWeek = LastReset(resetDay, resetMinutesUtc, nowUtc);
        var days = (atUtc - thisWeek).TotalDays;

        return (int)Math.Floor(days / 7) + 1;
    }

    /// <summary>
    /// The next time one slot comes round, at or after <paramref name="nowUtc"/>.
    ///
    /// A session already under way still counts as "next" until it ends, which is what lets the
    /// status bar say "raiding" rather than counting down to next week.
    /// </summary>
    public static DateTime NextOccurrence(RaidSlot slot, DateTime nowUtc)
    {
        var minutes = Wrap(slot.StartMinutesUtc);
        var forward = ((int)slot.Day - (int)nowUtc.DayOfWeek + 7) % 7;

        var start = nowUtc.Date.AddDays(forward).AddMinutes(minutes);

        // Still running counts as now rather than as a week away.
        if (start.AddMinutes(Math.Max(0, slot.DurationMinutes)) <= nowUtc)
            start = start.AddDays(7);

        return start;
    }

    /// <summary>Every slot's next occurrence, soonest first.</summary>
    public static IEnumerable<(RaidSlot Slot, DateTime StartUtc)> Upcoming(
        IReadOnlyList<RaidSlot> slots, DateTime nowUtc) =>
        slots.Select(s => (Slot: s, StartUtc: NextOccurrence(s, nowUtc))).OrderBy(x => x.StartUtc);

    /// <summary>The very next session, or null when the group has no schedule.</summary>
    public static (RaidSlot Slot, DateTime StartUtc)? Next(IReadOnlyList<RaidSlot> slots, DateTime nowUtc)
    {
        if (slots.Count == 0)
            return null;

        var next = Upcoming(slots, nowUtc).First();

        return next;
    }

    /// <summary>Whether a session is under way right now, and when it ends.</summary>
    public static bool Running(IReadOnlyList<RaidSlot> slots, DateTime nowUtc, out DateTime endsUtc)
    {
        endsUtc = default;

        foreach (var slot in slots)
        {
            var start = NextOccurrence(slot, nowUtc);

            if (start > nowUtc)
                continue;

            endsUtc = start.AddMinutes(Math.Max(0, slot.DurationMinutes));

            return true;
        }

        return false;
    }

    /// <summary>Minutes since midnight, folded into a day however far out of range they were.</summary>
    public static int Wrap(int minutes) => ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;

    /// <summary>"19:30", from minutes since midnight.</summary>
    public static string Clock(int minutes)
    {
        var wrapped = Wrap(minutes);

        return $"{wrapped / 60:00}:{wrapped % 60:00}";
    }

    /// <summary>
    /// The same weekly moment expressed in this machine's timezone.
    ///
    /// Both halves can move: 19:00 UTC on a Tuesday is 20:00 Tuesday in Berlin and 04:00 Wednesday in
    /// Sydney, so the day has to come back with the time or half the group reads the wrong evening.
    /// </summary>
    public static (DayOfWeek Day, int Minutes) ToLocal(DayOfWeek day, int minutesUtc, DateTime nowUtc)
    {
        var sample = NextOccurrence(new RaidSlot { Day = day, StartMinutesUtc = minutesUtc, DurationMinutes = 0 },
                                    nowUtc);

        var local = DateTime.SpecifyKind(sample, DateTimeKind.Utc).ToLocalTime();

        return (local.DayOfWeek, (local.Hour * 60) + local.Minute);
    }

    /// <summary>The other direction: a local weekly moment, written down in UTC.</summary>
    public static (DayOfWeek Day, int Minutes) ToUtc(DayOfWeek day, int minutesLocal, DateTime nowUtc)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToLocalTime();

        var forward = ((int)day - (int)now.DayOfWeek + 7) % 7;
        var local = now.Date.AddDays(forward).AddMinutes(Wrap(minutesLocal));

        var utc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

        return (utc.DayOfWeek, (utc.Hour * 60) + utc.Minute);
    }
}
