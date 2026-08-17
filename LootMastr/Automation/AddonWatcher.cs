using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace LootMastr.Automation;

/// <summary>One window that opened, and what it was holding when it did.</summary>
public sealed record AddonSighting(DateTime At, string Name, string Values);

/// <summary>One button press inside a loot window, as the game reported it.</summary>
public sealed record ButtonPress(DateTime At, string What);

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
    private readonly List<ButtonPress> events = new();
    private readonly List<AddonSighting> chestStates = new();

    public AddonWatcher()
    {
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAddonSetup);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, LootAddons, OnReceiveEvent);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "NeedGreed", OnChestRefresh);
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
    public IReadOnlyList<ButtonPress> Events => events;

    /// <summary>
    /// On by default while the assignment callback is still unknown. The button-press hook is
    /// scoped to the two loot windows and the window hook does nothing unless a chest is open, so
    /// the cost is nil — and having to remember a checkbox is what cost a recording session.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public void Clear()
    {
        sightings.Clear();
        events.Clear();
        chestStates.Clear();
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

        var what = $"{args.AddonName}  {kind}  param={received.EventParam}";

        // Held buttons repeat the same event; only the distinct ones say anything. Compared on the
        // event alone — slicing a fixed number of characters off the front assumed every addon
        // name was the same length, which two of them are not.
        if (events.Count > 0 && events[0].What == what)
            return;

        events.Insert(0, new ButtonPress(DateTime.Now, what));

        if (events.Count > Limit)
            events.RemoveRange(Limit, events.Count - Limit);
    }

    /// <summary>
    /// The chest's values every time the game rewrites them.
    ///
    /// Opening the window is not enough. What an award does to a coffer's row is the one thing a
    /// capture has never shown — the window is only snapshotted as it opens, and by then nothing has
    /// happened yet. A recording proved the coffer does <i>not</i> leave the chest afterwards, which
    /// leaves this as the only place a "spoken for" marker could be hiding.
    /// </summary>
    private void OnChestRefresh(AddonEvent type, AddonArgs args)
    {
        if (!Enabled)
            return;

        var values = DumpValues("NeedGreed");

        // The window refreshes once a second for its countdown, and that lives in the header. Only
        // the item blocks are compared, or the roll timer would push every real change back out.
        if (chestStates.Count > 0 && ItemBlocks(chestStates[0].Values) == ItemBlocks(values))
            return;

        chestStates.Insert(0, new AddonSighting(DateTime.Now, "NeedGreed", values));

        if (chestStates.Count > Limit)
            chestStates.RemoveRange(Limit, chestStates.Count - Limit);
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

    /// <summary>Everything past the seven header values, which is where the countdown ticks.</summary>
    private static string ItemBlocks(string dump)
    {
        var lines = dump.Split('\n');
        return lines.Length <= 7 ? dump : string.Join('\n', lines.Skip(7));
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
            foreach (var press in Enumerable.Reverse(events))
                builder.AppendLine($"  {press.At:HH:mm:ss.fff}  {press.What}");
        }

        builder.AppendLine();
        builder.AppendLine($"Chest contents each time they changed (recording: {Enabled}):");

        if (chestStates.Count == 0)
        {
            builder.AppendLine("  none recorded");
        }
        else
        {
            foreach (var state in chestStates.AsEnumerable().Reverse())
            {
                builder.AppendLine();
                builder.AppendLine($"  {state.At:HH:mm:ss.fff}  NeedGreed");
                builder.AppendLine(state.Values);
            }
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
        Services.AddonLifecycle.UnregisterListener(OnChestRefresh);
    }
}
