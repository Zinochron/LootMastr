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

namespace LootMastr.UI;

/// <summary>
/// Which groups this install knows, and who is in them.
///
/// A second window rather than a seventh tab, for a reason worth stating: the tabs are what a raid
/// leader has open <i>during</i> a pull. Adding a player, marking a second character, handing out
/// write access — none of that happens then, and all of it wants room the tab bar does not have.
///
/// Everything here edits the <b>current</b> static. Selecting a different one in the list switches to
/// it, which is one click and removes a whole second code path: no editor ever has to work on a
/// roster that is not the one the rest of the plugin is looking at.
/// </summary>
public sealed class StaticsWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly StaticStore statics;
    private readonly RosterStore roster;
    private readonly JobCatalog jobs;
    private readonly PartyReader party;

    private string newStaticName = string.Empty;
    private string newName = string.Empty;
    private string newWorld = string.Empty;
    private string renameBuffer = string.Empty;
    private string renaming = string.Empty;

    public StaticsWindow(Configuration config, StaticStore statics, RosterStore roster,
                         JobCatalog jobs, PartyReader party)
        : base("LootMastr — statics###LootMastrStatics")
    {
        this.config = config;
        this.statics = statics;
        this.roster = roster;
        this.jobs = jobs;
        this.party = party;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Open()
    {
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        DrawStaticList();

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(6f);

        DrawMembers();
    }

    private void DrawStaticList()
    {
        ImGui.TextUnformatted("Statics");
        Widgets.HelpMarker("One group each: its own roster, tier, kill counts and settings. A " +
                           "character can be in several — raiding in two groups, or bringing a " +
                           "second character to the same one.");
        ImGui.Separator();

        using (var table = ImRaii.Table("##statics", 4,
                                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Static");
                ImGui.TableSetupColumn("Players", ImGuiTableColumnFlags.WidthFixed, 64f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Sync", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                foreach (var profile in statics.All.ToList())
                    DrawStaticRow(profile);
            }
        }

        ImGuiHelpers.ScaledDummy(4f);

        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##newStatic", "Name of a new static", ref newStaticName, 48);

        ImGui.SameLine();

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newStaticName)))
        {
            if (ImGui.Button("Create"))
            {
                statics.Create(newStaticName);
                newStaticName = string.Empty;
            }
        }

        Widgets.HelpMarker("Creates an empty static and switches to it. Nothing leaves your machine " +
                           "until you switch synchronising on for it.");
    }

    private void DrawStaticRow(StaticProfile profile)
    {
        using var id = ImRaii.PushId(profile.Id);

        var current = statics.IsCurrent(profile);
        var primary = statics.Primary?.Id == profile.Id;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        if (renaming == profile.Id)
        {
            ImGui.SetNextItemWidth(-1f);

            if (ImGui.InputText("##rename", ref renameBuffer, 48, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                statics.Rename(profile, renameBuffer);
                renaming = string.Empty;
            }

            if (!ImGui.IsItemActive() && !ImGui.IsItemFocused())
                renaming = string.Empty;
        }
        else
        {
            ImGui.AlignTextToFramePadding();

            using (ImRaii.PushColor(ImGuiCol.Text, Widgets.Done, current))
                ImGui.TextUnformatted(profile.Name);

            Widgets.Tooltip(current ? "The static everything else is showing." : "Click Open to switch to this one.");

            if (primary)
            {
                ImGui.SameLine();
                Widgets.Coloured(Widgets.Wanted, "*");
                Widgets.Tooltip("Opens with the plugin.");
            }
        }

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(profile.Roster.Count.ToString());

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawSyncCell(profile);

        ImGui.TableNextColumn();

        using (ImRaii.Disabled(current))
        {
            if (ImGui.SmallButton("Open"))
                statics.Switch(profile.Id);
        }

        ImGui.SameLine(0f, 4f);

        if (ImGui.SmallButton(primary ? "Unpin" : "Pin"))
            statics.SetPrimary(profile);

        Widgets.Tooltip("Pin the static that should be open when the plugin starts.");

        ImGui.SameLine(0f, 4f);

        if (ImGui.SmallButton("Rename"))
        {
            renaming = profile.Id;
            renameBuffer = profile.Name;
        }

        ImGui.SameLine(0f, 4f);

        using (ImRaii.Disabled(statics.All.Count <= 1))
        {
            if (ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
                statics.Delete(profile);
        }

        Widgets.Tooltip(statics.All.Count <= 1
                            ? "The last static cannot be deleted — there would be nothing to show."
                            : "Ctrl+click to delete this static and everything in it.");
    }

    /// <summary>
    /// What syncing is doing for this static. Until the client exists this can only report the
    /// setting, and it says so rather than showing a green light nothing is behind.
    /// </summary>
    private static void DrawSyncCell(StaticProfile profile)
    {
        if (!profile.Sync.Enabled)
        {
            Widgets.Coloured(Widgets.Muted, "local only");
            Widgets.Tooltip("Nothing about this static leaves your machine.");
            return;
        }

        if (!profile.Sync.IsClaimed)
        {
            Widgets.Coloured(Widgets.Wanted, "not joined");
            Widgets.Tooltip("Switched on, but this client has not claimed a token yet.");
            return;
        }

        Widgets.Coloured(Widgets.Done, profile.Sync.Role.ToString().ToLowerInvariant());
        Widgets.Tooltip($"{profile.Sync.CharacterName} at {profile.Sync.Url}");
    }

    /// <summary>
    /// The line-up of whichever static is open.
    ///
    /// This is where adding a player and marking a second character live now. They used to sit in
    /// the roster tab's toolbar, next to reading gear — which put "who is in this group" and "what
    /// are they wearing tonight" in the same row, and only one of those is a raid-night action.
    /// </summary>
    private void DrawMembers()
    {
        var profile = statics.Current;

        ImGui.TextUnformatted($"Players in {profile.Name}");
        Widgets.HelpMarker("The order is a real setting: it is the last tiebreak when two players " +
                           "are otherwise equal candidates for a drop.");
        ImGui.Separator();

        DrawAddRow();
        ImGuiHelpers.ScaledDummy(4f);

        if (profile.Roster.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted,
                             "Nobody yet. Add players by hand, or press \"Add everyone in the party\".");
            return;
        }

        using var table = ImRaii.Table("##members", 4,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Main or alt", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 74f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        RosterMember? removing = null;

        foreach (var member in profile.Roster.ToList())
        {
            using var id = ImRaii.PushId(member.Key);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.DisplayName);

            ImGui.TableNextColumn();
            DrawJobCell(member);

            ImGui.TableNextColumn();
            DrawAltButton(member);

            ImGui.TableNextColumn();

            if (ImGui.SmallButton("^"))
                roster.Move(member, -1);

            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("v"))
                roster.Move(member, 1);

            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
                removing = member;

            Widgets.Tooltip("Ctrl+click to remove this player from the static.");
        }

        if (removing != null)
            roster.Remove(removing);
    }

    private void DrawAddRow()
    {
        if (ImGui.Button("Add everyone in the party"))
        {
            var added = roster.SyncFromParty(party.Read());

            Services.Chat.Print(added > 0
                                    ? $"LootMastr: added {added} player(s) to {statics.Current.Name}."
                                    : $"LootMastr: {statics.Current.Name} already had everyone in the party.");
        }

        Widgets.HelpMarker("Adds anyone in your party who is not in this static yet, and refreshes " +
                           "the job of everyone who already is. Nothing is ever removed.");

        ImGui.SameLine(0f, 16f);

        ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##newName", "Name", ref newName, 32);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##newWorld", "World", ref newWorld, 32);

        ImGui.SameLine();

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newName)))
        {
            if (ImGui.Button("Add"))
            {
                roster.Add(newName, newWorld);
                newName = string.Empty;
                newWorld = string.Empty;
            }
        }
    }

    private void DrawJobCell(RosterMember member)
    {
        var job = jobs.Get(member.JobId);
        var role = roster.RoleOf(member);

        Widgets.Icon(job.IconId, 18f);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        if (ImGui.SmallButton($"{job.Abbreviation}##job"))
            ImGui.OpenPopup("##jobPick");

        ImGui.SameLine();
        ImGui.TextDisabled(role.ToString());

        using var popup = ImRaii.Popup("##jobPick");
        if (!popup.Success)
            return;

        foreach (var group in jobs.All.Values
                                  .Where(j => j.Role != RaidRole.Unknown)
                                  .GroupBy(j => j.Role)
                                  .OrderBy(g => g.Key))
        {
            ImGui.TextDisabled(group.Key.ToString());

            foreach (var option in group.OrderBy(j => j.Abbreviation, StringComparer.Ordinal))
            {
                Widgets.Icon(option.IconId, 18f);
                ImGui.SameLine();

                if (ImGui.Selectable($"{option.Abbreviation}  {option.Name}##{option.Id}", member.JobId == option.Id))
                    roster.SetJob(member, option.Id);
            }
        }
    }

    /// <summary>Main or alt, as a button saying which it is. Nobody is one until somebody says so.</summary>
    private void DrawAltButton(RosterMember member)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, member.IsAlt ? Widgets.Augment : Widgets.Muted))
        {
            if (ImGui.SmallButton($"{(member.IsAlt ? "alt" : "main")}##alt"))
            {
                member.IsAlt = !member.IsAlt;
                member.Touch();
                config.Save();
            }
        }

        Widgets.Tooltip(member.IsAlt
                            ? "A second character, in the party to clear a fight again rather than to " +
                              "be geared. Takes nothing but the weapon stone and its material.\n\n" +
                              "Press to make them a main."
                            : "A main character — in the plan, and in the running for everything.\n\n" +
                              "Press to make this a second character.");

        if (member.IsAlt && !config.AltCharacters)
        {
            ImGui.SameLine();
            Widgets.Coloured(Widgets.Muted, "!");
            Widgets.Tooltip("Alt characters are switched off in Settings, so this player is left out " +
                            "of the plan entirely.");
        }
    }

    public void Dispose()
    {
    }
}
