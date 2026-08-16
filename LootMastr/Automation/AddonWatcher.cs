using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace LootMastr.Automation;

/// <summary>One window that opened, and what it was holding when it did.</summary>
public sealed record AddonSighting(DateTime At, string Name, string Values);

/// <summary>
/// Records every window that opens while the loot window is up.
///
/// This exists for one specific unknown: what the game puts on screen when the leader picks a
/// recipient for an item. Three captures taken in a live chest showed the chest itself perfectly
/// and the assignment step not at all, because by the time the capture button is pressed the
/// moment has passed. Rather than guess at an AtkValue payload — with someone's weekly lockout as
/// the cost of being wrong — this catches it as it happens.
///
/// Once that window is identified, this can go.
/// </summary>
public sealed class AddonWatcher : IDisposable
{
    private const int Limit = 30;

    /// <summary>Windows that are always up and would drown everything else out.</summary>
    private static readonly HashSet<string> Ignored =
    [
        "_FocusTargetInfo", "_PartyList", "_ActionBar", "_ParameterWidget", "_DTR", "_ToDoList",
        "Hud", "_TargetInfo", "_NaviMap", "_Notification", "_ChatLog", "_ScreenText",
    ];

    /// <summary>Windows whose button presses are worth writing down.</summary>
    private static readonly string[] LootAddons = ["NeedGreed", "NeedGreedTargeting"];

    /// <summary>Event kinds that fire constantly from moving the mouse, and would bury the clicks.</summary>
    private static readonly string[] NoisyEvents = ["MouseMove", "MouseOver", "MouseOut", "Drag", "Focus"];

    private readonly List<AddonSighting> sightings = new();
    private readonly List<string> events = new();

    public AddonWatcher()
    {
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAddonSetup);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, LootAddons, OnReceiveEvent);
    }

    public IReadOnlyList<AddonSighting> Sightings => sightings;

    /// <summary>
    /// Button presses inside the loot windows, as the game itself reports them.
    ///
    /// This is the piece that was missing. Which callback stands for "assign this item" was being
    /// inferred, and two inferences were wrong in two different ways — one pressed Greed only, the
    /// next did nothing. Recording the real click is not a guess: the event type and parameter here
    /// are exactly what the button sends, and clicking assign on two different rows shows how the
    /// row is encoded.
    /// </summary>
    public IReadOnlyList<string> Events => events;

    /// <summary>Off until it is wanted — it hooks every window in the game.</summary>
    public bool Enabled { get; set; }

    public void Clear()
    {
        sightings.Clear();
        events.Clear();
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!Enabled || args is not AddonReceiveEventArgs received)
            return;

        var kind = received.AtkEventType.ToString();

        foreach (var noisy in NoisyEvents)
        {
            if (kind.Contains(noisy, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff}  {args.AddonName}  {kind}  param={received.EventParam}";

        // Held down buttons repeat the same event; only the distinct ones say anything.
        if (events.Count > 0 && events[0][23..] == line[23..])
            return;

        events.Insert(0, line);

        if (events.Count > Limit)
            events.RemoveRange(Limit, events.Count - Limit);
    }

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (!Enabled)
            return;

        var name = args.AddonName;
        if (string.IsNullOrEmpty(name) || Ignored.Contains(name))
            return;

        // Only while a chest is on screen, so the log is the assignment flow and nothing else.
        if (!Services.GameGui.GetAddonByName("NeedGreed").IsVisible)
            return;

        sightings.Insert(0, new AddonSighting(DateTime.Now, name, DumpValues(name)));

        if (sightings.Count > Limit)
            sightings.RemoveRange(Limit, sightings.Count - Limit);

        Services.Log.Information($"Addon opened over the loot window: {name}");
    }

    private static string DumpValues(string name)
    {
        var addon = Services.GameGui.GetAddonByName(name);
        if (addon.IsNull)
            return "  <gone>";

        var builder = new StringBuilder();
        var index = 0;

        foreach (var value in addon.AtkValues)
        {
            var content = value.IsNull ? "<null>" : value.GetValue()?.ToString() ?? "<empty>";
            builder.AppendLine($"    [{index++}] {value.ValueType} = {content}");
        }

        return builder.Length == 0 ? "  <no values>" : builder.ToString().TrimEnd();
    }

    public void Append(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine($"Button presses in the loot windows (recording: {Enabled}):");

        if (events.Count == 0)
        {
            builder.AppendLine("  none recorded");
        }
        else
        {
            foreach (var line in Enumerable.Reverse(events))
                builder.AppendLine($"  {line}");
        }

        builder.AppendLine();
        builder.AppendLine($"Windows opened over the loot window (recording: {Enabled}):");

        if (sightings.Count == 0)
        {
            builder.AppendLine("  none recorded");
            return;
        }

        foreach (var sighting in sightings.AsEnumerable().Reverse())
        {
            builder.AppendLine();
            builder.AppendLine($"  {sighting.At:HH:mm:ss.fff}  {sighting.Name}");
            builder.AppendLine(sighting.Values);
        }
    }

    public void Dispose()
    {
        Services.AddonLifecycle.UnregisterListener(OnAddonSetup);
        Services.AddonLifecycle.UnregisterListener(OnReceiveEvent);
    }
}
