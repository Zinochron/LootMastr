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

    private readonly List<AddonSighting> sightings = new();

    public AddonWatcher()
    {
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAddonSetup);
    }

    public IReadOnlyList<AddonSighting> Sightings => sightings;

    /// <summary>Off until it is wanted — it hooks every window in the game.</summary>
    public bool Enabled { get; set; }

    public void Clear() => sightings.Clear();

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

    public void Dispose() => Services.AddonLifecycle.UnregisterListener(OnAddonSetup);
}
