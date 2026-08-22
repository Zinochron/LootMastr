using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr.Automation;

/// <summary>
/// Says something before the raid starts.
///
/// Hangs off the framework tick and <b>not</b> off any window's draw loop. That is the whole design
/// constraint: <c>SyncClient.Poll</c> only runs while a LootMastr window is open, and a reminder
/// that needs the window open reminds exactly the people who are not looking.
///
/// The schedule belongs to the static; when and how to be told belongs to whoever is being told. A
/// group agreeing to raid on Thursdays has not agreed that everyone wants a toast an hour before.
/// </summary>
public sealed class RaidReminder : IDisposable
{
    /// <summary>How often the clock is consulted. A minute's warning does not need finer than this.</summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    private readonly Configuration config;
    private readonly StaticStore statics;

    /// <summary>
    /// Which warnings have already gone out, keyed by the moment they were about.
    ///
    /// Keyed by the occurrence rather than counted down to, so a client that was asleep through the
    /// hour mark does not fire it late, and one left running for a month does not accumulate.
    /// </summary>
    private readonly HashSet<string> fired = [];

    private IDtrBarEntry? bar;
    private DateTime lastCheck = DateTime.MinValue;

    public RaidReminder(Configuration config, StaticStore statics)
    {
        this.config = config;
        this.statics = statics;

        Services.Framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        if (now - lastCheck < CheckEvery)
            return;

        lastCheck = now;

        var slots = statics.Current.Settings.Schedule;
        var next = RaidCalendar.Next(slots, now);

        UpdateBar(slots, next, now);

        if (next == null)
            return;

        Announce(next.Value.StartUtc, now);
        Forget(now);
    }

    /// <summary>
    /// Fires each lead time once for each session.
    ///
    /// The window is one check wide rather than "less than N minutes away", because the second form
    /// is true for the whole hour before an hour's warning and would fire once and then be silent
    /// for the wrong reason — or fire every tick, depending on which way the guard is written.
    /// </summary>
    private void Announce(DateTime startUtc, DateTime now)
    {
        foreach (var lead in config.ReminderMinutes.Distinct().Where(m => m > 0).OrderByDescending(m => m))
        {
            var at = startUtc.AddMinutes(-lead);

            if (now < at || now - at > CheckEvery)
                continue;

            var key = $"{startUtc:O}#{lead}";

            if (!fired.Add(key))
                continue;

            Say($"Raid in {Describe(lead)}", $"{statics.Current.Name} starts at " +
                                             $"{startUtc.ToLocalTime():HH:mm}.");
        }

        // The start itself, which is the one nobody wants to miss and the one a lead time cannot
        // express: a warning "0 minutes before" reads as a mistake.
        if (now >= startUtc && now - startUtc <= CheckEvery && fired.Add($"{startUtc:O}#start"))
            Say("Raid now", $"{statics.Current.Name} starts now.");
    }

    private void Say(string title, string body)
    {
        if (config.RemindByNotification)
        {
            Services.Notifications.AddNotification(new Notification
            {
                Title = title,
                Content = body,
                Type = NotificationType.Info,
                InitialDuration = TimeSpan.FromSeconds(10),
            });
        }

        if (config.RemindByChat)
            Services.Chat.Print($"LootMastr: {title} — {body}");
    }

    /// <summary>
    /// A countdown beside the clock, for people who would rather glance than be interrupted.
    ///
    /// Created the first time it is wanted and torn down the moment it is not, so a group with no
    /// schedule, somebody with the setting off, and a raid five days away all leave the status bar
    /// alone rather than parking something permanent in it.
    /// </summary>
    private void UpdateBar(IReadOnlyList<RaidSlot> slots, (RaidSlot Slot, DateTime StartUtc)? next,
                           DateTime now)
    {
        if (!config.RemindInDtrBar || next == null || !RaidCalendar.CountdownDue(slots, now))
        {
            bar?.Remove();
            bar = null;
            return;
        }

        bar ??= Services.DtrBar.Get("LootMastr");

        var text = RaidCalendar.Running(slots, now, out var ends)
                       ? $"Raiding, {Describe((int)(ends - now).TotalMinutes)} left"
                       : $"Raid in {Describe((int)(next.Value.StartUtc - now).TotalMinutes)}";

        bar.Text = text;
        bar.Tooltip = $"{statics.Current.Name} — {next.Value.StartUtc.ToLocalTime():ddd HH:mm}";
        bar.Shown = true;
    }

    /// <summary>Hours where there are hours, minutes where there are not. "90m" helps nobody.</summary>
    private static string Describe(int minutes)
    {
        if (minutes < 1)
            return "under a minute";

        if (minutes < 60)
            return $"{minutes}m";

        var hours = minutes / 60;
        var rest = minutes % 60;

        return rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }

    /// <summary>Drops keys for sessions that are well past, so the set cannot grow without end.</summary>
    private void Forget(DateTime now)
    {
        if (fired.Count < 64)
            return;

        fired.RemoveWhere(key =>
        {
            var at = key.Split('#')[0];

            return DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) &&
                   now - when > TimeSpan.FromDays(2);
        });
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;

        bar?.Remove();
        bar = null;
    }
}
