using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using LootMastr.Data;
using LootMastr.Roster;
using LootMastr.Sync;

namespace LootMastr.UI;

public sealed class MainWindow : Window, IDisposable
{
    private readonly List<ITab> tabs;
    private readonly StaticStore statics;
    private readonly TierCatalog tiers;
    private readonly SyncClient sync;
    private readonly Configuration config;
    private readonly Action openStatics;
    private readonly Action openTiers;

    /// <summary>Set to have the next draw jump to a specific tab, e.g. when opened via the config button.</summary>
    private string? pendingTabId;

    public MainWindow(IEnumerable<ITab> tabs, StaticStore statics, TierCatalog tiers, SyncClient sync,
                      Configuration config, Action openStatics, Action openTiers)
        : base($"LootMastr {Build.Version}###LootMastrMain")
    {
        this.tabs = new List<ITab>(tabs);
        this.statics = statics;
        this.tiers = tiers;
        this.sync = sync;
        this.config = config;
        this.openStatics = openStatics;
        this.openTiers = openTiers;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(1000, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void OpenAt(string tabId)
    {
        pendingTabId = tabId;
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        // The one place syncing is driven from. It runs while a window is open and stops when it is
        // closed, which is the right shape: nobody needs their roster kept fresh in the background
        // while they are not looking at it.
        sync.Poll();

        DrawHeader();

        using var bar = ImRaii.TabBar("##LootMastrTabs");
        if (!bar.Success)
            return;

        foreach (var tab in tabs)
        {
            if (!tab.Available)
                continue;

            if (!tab.UsefulToReaders && !config.CanWrite)
                continue;

            var flags = ImGuiTabItemFlags.None;
            if (pendingTabId == tab.Id)
            {
                flags |= ImGuiTabItemFlags.SetSelected;
                pendingTabId = null;
            }

            using var item = ImRaii.TabItem($"{tab.Title}###{tab.Id}", flags);
            if (!item.Success)
                continue;

            using var child = ImRaii.Child($"##{tab.Id}Body", Vector2.Zero, false);
            if (child.Success)
                tab.Draw();
        }
    }

    /// <summary>
    /// Which static and which tier every tab below is talking about.
    ///
    /// It is above the tab bar and not inside a tab because it is true of all of them at once, and
    /// because getting it wrong is expensive in a way nothing else in this window is: ticking a
    /// piece off in the wrong static edits the wrong group's sheet, and nothing downstream can tell
    /// that happened.
    /// </summary>
    private void DrawHeader()
    {
        var profile = statics.Current;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Static");
        ImGui.SameLine(0f, 4f);

        if (ImGui.SmallButton($"{profile.Name}##staticPick"))
            ImGui.OpenPopup("##staticMenu");

        Widgets.Tooltip("Switch between the groups this install knows.");

        using (var popup = ImRaii.Popup("##staticMenu"))
        {
            if (popup.Success)
            {
                foreach (var option in statics.All.ToList())
                {
                    if (ImGui.Selectable($"{option.Name}##{option.Id}", statics.IsCurrent(option)))
                    {
                        if (statics.Switch(option.Id))
                            sync.Refresh();
                    }

                    ImGui.SameLine();
                    ImGui.TextDisabled($"{option.Roster.Count} player(s)");
                }

                ImGui.Separator();

                if (ImGui.Selectable("Manage statics…"))
                    openStatics();
            }
        }

        ImGui.SameLine(0f, 4f);

        if (ImGui.SmallButton("...##manageStatics"))
            openStatics();

        Widgets.Tooltip("Add players, mark second characters, create or delete statics.");

        ImGui.SameLine(0f, 16f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Tier");
        ImGui.SameLine(0f, 4f);

        var tier = tiers.Tier;
        var tierName = string.IsNullOrWhiteSpace(tier.Name) ? "no tier" : tier.Name;

        if (ImGui.SmallButton($"{tierName}##tierPick"))
            ImGui.OpenPopup("##tierMenu");

        Widgets.Tooltip("Which tier this static is running. Switching reloads the definition from disk.");

        using (var popup = ImRaii.Popup("##tierMenu"))
        {
            if (popup.Success)
            {
                // Which tier a static runs is the group's decision, and switching it here replaces
                // the definition wholesale — the loudest edit in the plugin, from its smallest
                // control.
                using (ImRaii.Disabled(!config.CanWrite))
                {
                    foreach (var (id, name) in TierCatalog.AvailableTiers())
                    {
                        if (ImGui.Selectable($"{name}##{id}", id == profile.ActiveTierId))
                            tiers.Load(id);
                    }
                }

                ImGui.Separator();

                if (ImGui.Selectable("Edit this tier…"))
                    openTiers();
            }
        }

        ImGui.SameLine(0f, 4f);

        if (ImGui.SmallButton("...##editTier"))
            openTiers();

        Widgets.Tooltip("What this tier drops, what its books buy, and what the vendor charges.");

        ImGui.SameLine(0f, 16f);
        DrawSyncButton(profile);

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);
    }

    /// <summary>
    /// State and switch in one control: what syncing is doing, and the way to turn it off.
    ///
    /// Grey and disabled while this static is local only — which is every static until somebody
    /// switches it on, and which has to look like a deliberate setting rather than like a failure.
    /// </summary>
    private void DrawSyncButton(StaticProfile profile)
    {
        var setup = profile.Sync;
        var status = sync.StatusOf(profile);
        var claimed = setup.IsClaimed;

        var (colour, label, tip) = status switch
        {
            SyncStatus.Off => (Widgets.Muted, "local",
                               "This static never leaves your machine.\n\nOpens Manage statics."),

            SyncStatus.NotJoined => (Widgets.Wanted, "not joined",
                                     "Syncing is on, but this client has not claimed a token yet.\n\n" +
                                     "Opens Manage statics."),

            SyncStatus.Working => (Widgets.Wanted, "syncing…", sync.Message),

            SyncStatus.Error => (Widgets.Bad, "problem", $"{sync.Message}\n\nClick to try again."),

            _ => (Widgets.Done, setup.Role.ToString().ToLowerInvariant(),
                  $"Synced as {setup.CharacterName}" +
                  (setup.LastSyncUtc is { } when ? $", last at {when.ToLocalTime():HH:mm}. " : ". ") +
                  sync.Message +
                  "\n\nClick to pull now."),
        };

        using (ImRaii.PushColor(ImGuiCol.Text, colour))
        {
            // Pulling is what somebody wants from this button nine times in ten: the label is the
            // question "is what I am looking at current", and a click is the shortest way to make
            // the answer yes. Only when there is nothing to pull from does it fall back to opening
            // the window that can fix that.
            if (ImGui.SmallButton($"{label}##sync"))
            {
                if (claimed)
                    sync.Pull(profile, manual: true);
                else
                    openStatics();
            }
        }

        Widgets.Tooltip(string.IsNullOrWhiteSpace(tip) ? "Syncing." : tip);

        // The pretence from the debug tab, wherever anybody is looking. Never letting it be invisible
        // is the only thing that keeps it from becoming a bug report about read-only being stuck.
        if (config.PretendRole is not { } pretending)
            return;

        ImGui.SameLine(0f, 6f);
        Widgets.Coloured(Widgets.Wanted, $"(as {pretending.ToString().ToLowerInvariant()})");
        Widgets.Tooltip("The debug tab is drawing every screen as though you had this role. It " +
                        "changes what the interface offers and nothing the server allows.");
    }

    public void Dispose()
    {
        foreach (var tab in tabs)
            tab.Dispose();

        tabs.Clear();
    }
}
