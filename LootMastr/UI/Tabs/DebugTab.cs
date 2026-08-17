using System;
using System.IO;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using LootMastr.Automation;
using LootMastr.Data;

namespace LootMastr.UI.Tabs;

/// <summary>
/// Reads the loot window out loud. This exists because the one thing LootMastr cannot do yet —
/// pick the loot recipient — has to be watched in a real duty before it can be written, and a
/// failure with no visible reason is the worst kind.
/// </summary>
public sealed class DebugTab : ITab
{
    private readonly LootWindowReader loot;
    private readonly PartyReader party;
    private readonly TierCatalog tiers;
    private readonly ObtainTracker tracker;
    private readonly AddonWatcher watcher;
    private readonly ItemCatalog catalog;
    private readonly JobCatalog jobs;

    private string lastCapture = string.Empty;
    private string lastProbe = string.Empty;

    public DebugTab(LootWindowReader loot, PartyReader party, TierCatalog tiers, ObtainTracker tracker,
                    AddonWatcher watcher, ItemCatalog items, JobCatalog jobs)
    {
        this.loot = loot;
        this.party = party;
        this.tiers = tiers;
        this.tracker = tracker;
        this.watcher = watcher;
        this.catalog = items;
        this.jobs = jobs;
    }

    public string Title => "Debug";
    public string Id => "debug";

    public void Draw()
    {
        DrawLiveState();

        ImGuiHelpers.ScaledDummy(8f);
        DrawLootItems();

        ImGuiHelpers.ScaledDummy(8f);
        DrawAssignmentRecorder();

        ImGuiHelpers.ScaledDummy(8f);
        DrawCapture();

        ImGuiHelpers.ScaledDummy(8f);
        DrawChatProbe();
    }

    private void DrawLiveState()
    {
        ImGui.TextUnformatted("Live state");
        ImGui.Separator();

        var items = loot.Read();

        Row("Loot window open", loot.WindowOpen.ToString());
        Row("Items read", items.Count.ToString());
        Row("Party leader", party.LocalPlayerIsLeader() ? "you" : "someone else");
        Row("Lootmaster mode", loot.LootmasterActive().ToString());
        Row("Territory", Services.ClientState.TerritoryType.ToString());

        var encounter = tiers.EncounterInTerritory(Services.ClientState.TerritoryType);
        Row("Recognised as", encounter?.Name ?? "not learned yet");
        Row("Party size", Services.Party.Length.ToString());
    }

    private static void Row(string label, string value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(200f * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled(value);
    }

    private void DrawLootItems()
    {
        ImGui.TextUnformatted("Loot window contents");
        ImGui.Separator();

        var items = loot.Read();
        if (items.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing in the window.");
            return;
        }

        using var table = ImRaii.Table("##rawLoot", 7,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item id", ImGuiTableColumnFlags.WidthFixed, 66f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Roll state", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Matched", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var item in items)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Index.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.ItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.RollState.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.RollResult.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Mode.ToString());

            ImGui.TableNextColumn();
            if (item.IsTierLoot)
                Widgets.Coloured(Widgets.Done, item.What);
            else
                Widgets.Coloured(Widgets.Muted, "—");
        }
    }

    /// <summary>
    /// The one thing still needed to finish automatic assignment: what the game puts on screen when
    /// the leader picks who gets an item. Captures taken so far all showed the chest and not that
    /// step, because pressing the capture button means the moment has already passed.
    /// </summary>
    private void DrawAssignmentRecorder()
    {
        ImGui.TextUnformatted("Record the assignment window");
        ImGui.Separator();

        var enabled = watcher.Enabled;
        if (ImGui.Checkbox("Record windows that open over the loot window", ref enabled))
            watcher.Enabled = enabled;

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            watcher.Clear();

        Widgets.Coloured(Widgets.Muted,
                         "Turn this on, open a chest as Lootmaster, then press the assign button on " +
                         "two different items by hand and write a capture file. Both the button " +
                         "presses and any window they open are recorded — the two together are what " +
                         "the assignment code still needs.");

        if (watcher.Events.Count > 0)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextDisabled("Button presses, newest first:");

            foreach (var press in watcher.Events)
                ImGui.TextUnformatted($"{press.At:HH:mm:ss}  {press.What}");
        }

        if (watcher.Sightings.Count == 0)
        {
            if (watcher.Events.Count == 0)
                Widgets.Coloured(Widgets.Muted, enabled ? "Nothing recorded yet." : "Not recording.");

            return;
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextDisabled("Windows opened:");

        foreach (var sighting in watcher.Sightings)
        {
            ImGui.TextUnformatted($"{sighting.At:HH:mm:ss}  {sighting.Name}");
            Widgets.Tooltip(sighting.Values);
        }
    }

    private void DrawCapture()
    {
        ImGui.TextUnformatted("Capture the loot addon");
        Widgets.HelpMarker("Writes everything the loot window is holding to a text file next to the " +
                           "config, including the raw values behind it. That file is what finishes " +
                           "the assignment code — take it with a Lootmaster chest on screen.");
        ImGui.Separator();

        if (ImGui.Button("Write capture file"))
            lastCapture = WriteCapture();

        if (!string.IsNullOrEmpty(lastCapture))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(lastCapture);
        }

        ImGuiHelpers.ScaledDummy(6f);
        DrawStatProbe();
    }

    /// <summary>
    /// The game data behind the damage estimate, dumped next to something checkable.
    ///
    /// Four things about the stat sheets could only be read as shapes, not values — most of all
    /// which <c>ParamGrow</c> columns are the damage formula's level constants, which have no
    /// column names at all. Every one of them is a single look away from settled, and a damage
    /// model built on a plausible reading instead would be quietly wrong by some percentage nobody
    /// could find.
    /// </summary>
    private void DrawStatProbe()
    {
        ImGui.TextUnformatted("Probe the stat data");
        Widgets.HelpMarker("Writes the level table, job modifiers, your own measured attributes and " +
                           "your melds to a file, so they can be held against the character window.\n\n" +
                           "Two of the answers only exist in the right moment: run it once normally, " +
                           "and once with somebody's examine window open.");
        ImGui.Separator();

        if (ImGui.Button("Write stat probe"))
            lastProbe = WriteProbe();

        if (string.IsNullOrEmpty(lastProbe))
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(lastProbe);
    }

    private string WriteProbe()
    {
        try
        {
            var path = Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(),
                                    $"stat-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var report = StatProbe.Run(catalog, jobs);

            File.WriteAllText(path, report);
            Services.Log.Information(report);

            return Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not write the stat probe.");
            return ex.Message;
        }
    }

    private unsafe string WriteCapture()
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine($"LootMastr {Build.Version} capture {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Territory: {Services.ClientState.TerritoryType}");
            builder.AppendLine($"Leader: {party.LocalPlayerIsLeader()}");
            builder.AppendLine();

            builder.AppendLine("Party:");
            foreach (var player in party.Read())
                builder.AppendLine($"  {player.Name}@{player.World} job {player.JobId} leader={player.IsLeader}");

            builder.AppendLine();
            builder.AppendLine("Loot items:");
            foreach (var item in loot.Read())
            {
                builder.AppendLine($"  [{item.Index}] {item.ItemId} \"{item.Name}\" x{item.Count} " +
                                   $"state={item.RollState} result={item.RollResult} mode={item.Mode} " +
                                   $"weekly={item.WeeklyLootItem} matched={(item.IsTierLoot ? item.What : "-")}");
            }

            var agent = AgentLoot.Instance();
            builder.AppendLine();
            builder.AppendLine("AgentLoot:");
            if (agent == null)
            {
                builder.AppendLine("  null");
            }
            else
            {
                builder.AppendLine($"  NumItems={agent->NumItems} SelectedSlotIndex={agent->SelectedSlotIndex} " +
                                   $"HoveredSlotIndex={agent->HoveredSlotIndex} HoveredItemId={agent->HoveredItemId} " +
                                   $"AddonId={agent->AddonId} shown={agent->IsAddonShown()}");
            }

            builder.AppendLine();
            builder.AppendLine("AddonNeedGreed AtkValues:");
            AppendAddonValues(builder, "NeedGreed");

            // Captured too, so a shop that failed to read can be diagnosed from the same file.
            foreach (var name in new[] { "ShopExchangeCurrency", "ShopExchangeItem", "InclusionShop", "Shop" })
            {
                var shop = Services.GameGui.GetAddonByName(name);
                if (shop.IsNull || !shop.IsVisible)
                    continue;

                builder.AppendLine();
                builder.AppendLine($"{name} AtkValues:");
                AppendAddonValues(builder, name);
            }

            watcher.Append(builder);

            var path = Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(),
                                    $"loot-capture-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            File.WriteAllText(path, builder.ToString());
            Services.Log.Information($"Wrote loot capture to {path}");

            return Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not write the loot capture.");
            return ex.Message;
        }
    }

    /// <summary>
    /// The addon's raw values. In Lootmaster mode the recipient list lives in here, which is
    /// exactly what is needed to finish <see cref="LootAssigner.PerformAssignment"/>: the strings
    /// tell you where the party member rows sit, and the indices tell you what to fire back.
    /// </summary>
    private static unsafe void AppendAddonValues(StringBuilder builder, string addonName)
    {
        var addon = Services.GameGui.GetAddonByName(addonName);
        if (addon.IsNull)
        {
            builder.AppendLine("  addon not found");
            return;
        }

        builder.AppendLine($"  visible={addon.IsVisible} ready={addon.IsReady} " +
                           $"AtkValuesCount={addon.AtkValuesCount}");

        if (addonName == "NeedGreed")
        {
            var needGreed = (AddonNeedGreed*)addon.Address;
            if (needGreed != null)
                builder.AppendLine($"  NumItems={needGreed->NumItems} " +
                                   $"SelectedItemIndex={needGreed->SelectedItemIndex}");
        }

        var index = 0;

        foreach (var value in addon.AtkValues)
        {
            var content = value.IsNull ? "<null>" : value.GetValue()?.ToString() ?? "<empty>";
            builder.AppendLine($"    [{index++}] {value.ValueType} = {content}");
        }
    }

    private void DrawChatProbe()
    {
        ImGui.TextUnformatted("Chat lines the tracker considered");
        Widgets.HelpMarker("Every message on a loot channel that carried an item link, and what the " +
                           "tracker did with it. If something was received but never ticked off, the " +
                           "reason is here.");
        ImGui.Separator();

        var enabled = tracker.Enabled;
        if (ImGui.Checkbox("Tick lists off from chat", ref enabled))
            tracker.Enabled = enabled;

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            tracker.Clear();

        if (tracker.Recent.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing seen yet.");
            return;
        }

        using var table = ImRaii.Table("##chatProbe", 4,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 62f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Line");
        ImGui.TableSetupColumn("Verdict", ImGuiTableColumnFlags.WidthFixed, 170f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var sighting in tracker.Recent)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sighting.At.ToString("HH:mm:ss"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sighting.Type);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sighting.Text);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sighting.Verdict);
        }
    }

    public void Dispose() { }
}
