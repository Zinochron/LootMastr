using System;
using System.Collections.Generic;
using ECommons.Automation;

namespace LootMastr.Automation;

/// <summary>
/// Posts assignments to party chat. Off by default and never automatic on its own: this is the one
/// part of the plugin that talks to other people, and a wrong line here is worse than a wrong tick
/// in a table.
/// </summary>
public sealed class ChatAnnouncer
{
    private readonly Configuration config;
    private readonly SafetyGuard guard;

    private DateTime lastSent = DateTime.MinValue;

    public ChatAnnouncer(Configuration config, SafetyGuard guard)
    {
        this.config = config;
        this.guard = guard;
    }

    public string LastResult { get; private set; } = string.Empty;

    public bool Announce(IReadOnlyList<LootDecision> decisions)
    {
        var verdict = guard.CheckAnnounce();
        if (!verdict.Ok)
        {
            LastResult = verdict.Reason;
            return false;
        }

        // The game drops messages sent faster than roughly one a second, and a burst reads as spam
        // to everyone in the party either way.
        if ((DateTime.UtcNow - lastSent).TotalMilliseconds < config.ActionDelayMs)
        {
            LastResult = "Slow down — waiting out the send delay.";
            return false;
        }

        var line = Compose(decisions);
        if (string.IsNullOrEmpty(line))
        {
            LastResult = "Nothing to announce.";
            return false;
        }

        try
        {
            Chat.SendMessage(line);
            lastSent = DateTime.UtcNow;
            LastResult = "Announced.";
            return true;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not send the announcement.");
            LastResult = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// One line for the whole window rather than one per item, so a four-drop chest does not push
    /// everything else out of the party chat log.
    /// </summary>
    private static string Compose(IReadOnlyList<LootDecision> decisions)
    {
        var parts = new List<string>();

        foreach (var decision in decisions)
        {
            if (decision.Winner == null)
                continue;

            parts.Add($"{decision.Item.What} > {decision.Winner.Name}");
        }

        return parts.Count == 0 ? string.Empty : $"/p LootMastr: {string.Join(", ", parts)}";
    }
}
