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
    private readonly SyncClient sync;

    private string newStaticName = string.Empty;
    private string newName = string.Empty;
    private string newWorld = string.Empty;
    private string renameBuffer = string.Empty;
    private string renaming = string.Empty;
    private bool renameFresh;

    // Sync form. The password is a field on this window and nowhere else: it goes to the server
    // once, is cleared the moment it has been sent, and is never written to the config.
    private string syncUrl = string.Empty;
    private string syncName = string.Empty;
    private string syncPassword = string.Empty;
    private string syncCharacter = string.Empty;
    private string formFor = string.Empty;

    public StaticsWindow(Configuration config, StaticStore statics, RosterStore roster,
                         JobCatalog jobs, PartyReader party, SyncClient sync)
        : base("LootMastr — statics###LootMastrStatics")
    {
        this.config = config;
        this.statics = statics;
        this.roster = roster;
        this.jobs = jobs;
        this.party = party;
        this.sync = sync;

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
        sync.Poll();

        DrawStaticList();

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(6f);

        DrawSharing();

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(6f);

        DrawMembers();
    }

    /// <summary>
    /// Whether this static lives on a server, and who may change it there.
    ///
    /// The section says what leaves the machine before it offers to send it. That is not politeness:
    /// character names are the whole payload, and a plugin that uploads them because a checkbox was
    /// convenient to tick is a plugin that decided something for somebody.
    /// </summary>
    private void DrawSharing()
    {
        var profile = statics.Current;
        var setup = profile.Sync;

        // Open until there is a token, shut once there is one. The form matters exactly once, on the
        // evening somebody sets this up; after that it is four lines about a server that has not
        // changed, sitting above the list people actually came to read.
        if (!setup.IsClaimed)
            ImGui.SetNextItemOpen(true, ImGuiCond.Once);

        // Labelled by the static, identified by nothing — or renaming a static would fold the
        // section and lose whatever it was showing.
        var open = ImGui.CollapsingHeader($"Sharing {profile.Name}###sharing");

        Widgets.HelpMarker("Syncing puts this static on a server so the rest of the group can read " +
                           "it — who needs what, and what to buy this week.\n\n" +
                           "What is uploaded: the roster (names, worlds, jobs, gear sets, what each " +
                           "player has been given), the tier, the kill counts and the settings. What " +
                           "is not: anything about your machine, and never your password.");

        if (!open)
        {
            // Folded away, a problem would be invisible. The one line that has to survive is the one
            // saying something went wrong.
            if (sync.StatusOf(profile) == SyncStatus.Error && !string.IsNullOrEmpty(sync.Message))
            {
                using var indent = ImRaii.PushIndent();
                Widgets.Coloured(Widgets.Bad, sync.Message);
            }

            return;
        }

        if (!string.IsNullOrEmpty(sync.Message))
        {
            var colour = sync.StatusOf(profile) switch
            {
                SyncStatus.Error => Widgets.Bad,
                SyncStatus.Working => Widgets.Wanted,
                _ => Widgets.Muted,
            };

            Widgets.Coloured(colour, sync.Message);
            ImGuiHelpers.ScaledDummy(4f);
        }

        if (setup.IsClaimed)
        {
            DrawConnected(profile, setup);
            return;
        }

        DrawJoinForm(profile);
    }

    private void DrawConnected(StaticProfile profile, SyncSetup setup)
    {
        ImGui.TextDisabled($"{setup.RemoteName} at {setup.Url}");
        ImGui.TextDisabled($"as {setup.CharacterName} — {setup.Role.ToString().ToLowerInvariant()}" +
                           (setup.Revision > 0 ? $", revision {setup.Revision}" : string.Empty));

        using (ImRaii.Disabled(sync.IsBusy))
        {
            if (ImGui.Button("Pull now"))
                sync.Pull(profile, manual: true);

            Widgets.HelpMarker("Takes the server's copy. Anything you have changed here and not " +
                               "pushed is replaced.");

            ImGui.SameLine();

            using (ImRaii.Disabled(setup.Role == StaticRole.Read))
            {
                if (ImGui.Button("Push now"))
                    sync.Push(profile, manual: true);
            }

            Widgets.HelpMarker(setup.Role == StaticRole.Read
                                   ? "This character may only read. An admin can change that."
                                   : "Sends your copy. Pushing happens on its own a couple of seconds " +
                                     "after a change; this is for when you would rather not wait.");

            ImGui.SameLine();

            if (ImGui.Button("Members"))
                sync.LoadMembers(profile);

            Widgets.HelpMarker("Who has joined this static, and what each of them may do.");
        }

        ImGui.SameLine(0f, 20f);

        if (ImGui.Button("Stop syncing") && ImGui.GetIO().KeyCtrl)
        {
            profile.Sync = new SyncSetup();
            config.Save();
            sync.Refresh();
        }

        Widgets.Tooltip("Ctrl+click. Forgets this client's token and leaves the static on the server " +
                        "untouched — everyone else keeps working, and this copy goes back to local only.");

        DrawPermissions(profile, setup);
    }

    private void DrawPermissions(StaticProfile profile, SyncSetup setup)
    {
        if (sync.Members.Count == 0)
            return;

        ImGuiHelpers.ScaledDummy(6f);

        using var table = ImRaii.Table("##perms", 3,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("May", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Joined", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var member in sync.Members)
        {
            using var id = ImRaii.PushId(member.Character);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.Character);

            ImGui.TableNextColumn();

            // Only an admin may hand out rights, and the server checks it again. This is the button
            // being honest about what it would achieve, not the security.
            using (ImRaii.Disabled(!config.IsAdmin || sync.IsBusy))
            {
                if (ImGui.SmallButton($"{member.Role.ToString().ToLowerInvariant()}##role"))
                    ImGui.OpenPopup("##rolePick");
            }

            if (!config.IsAdmin)
                Widgets.Tooltip("Only an admin can change what somebody may do.");

            using (var popup = ImRaii.Popup("##rolePick"))
            {
                if (popup.Success)
                {
                    foreach (var role in new[] { StaticRole.Read, StaticRole.Write, StaticRole.Admin })
                    {
                        if (ImGui.Selectable(RoleLabel(role), member.Role == role))
                            sync.SetRole(profile, member.Character, role);
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(member.ClaimedAt is { } when ? when.ToLocalTime().ToString("d MMM") : "—");
        }
    }

    private static string RoleLabel(StaticRole role) => role switch
    {
        StaticRole.Read => "read — see the roster and the plan",
        StaticRole.Write => "write — edit the roster, tier and settings",
        _ => "admin — that, and hand out rights",
    };

    private void DrawJoinForm(StaticProfile profile)
    {
        // Prefilled once per static, not every frame, or typing would be impossible.
        if (formFor != profile.Id)
        {
            formFor = profile.Id;
            syncUrl = profile.Sync.Url;
            syncName = string.IsNullOrWhiteSpace(profile.Sync.RemoteName) ? profile.Name : profile.Sync.RemoteName;
            syncPassword = string.Empty;
            syncCharacter = LocalCharacter();
        }

        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##url", "https://example.com/lootmastr", ref syncUrl, 200);
        ImGui.SameLine();
        ImGui.TextDisabled("Server");

        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##remoteName", "Name on the server", ref syncName, 48);
        ImGui.SameLine();
        ImGui.TextDisabled("Static");

        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##password", "Password", ref syncPassword, 128, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        ImGui.TextDisabled("Password");
        Widgets.HelpMarker("Sent once, to prove you belong here, and then forgotten. What is stored " +
                           "is a token bound to the character below — so this file never contains " +
                           "the password, and a token is worth less than one.");

        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##character", "Character name", ref syncCharacter, 48);
        ImGui.SameLine();
        ImGui.TextDisabled("As");
        Widgets.HelpMarker("Rights are per character. This is the one the server will know you by.");

        ImGuiHelpers.ScaledDummy(4f);

        var ready = !string.IsNullOrWhiteSpace(syncUrl) && !string.IsNullOrWhiteSpace(syncName) &&
                    !string.IsNullOrWhiteSpace(syncPassword) && !string.IsNullOrWhiteSpace(syncCharacter);

        using (ImRaii.Disabled(!ready || sync.IsBusy))
        {
            if (ImGui.Button("Join"))
            {
                sync.Join(profile, syncUrl.Trim(), syncName.Trim(), syncPassword, syncCharacter.Trim());
                syncPassword = string.Empty;
            }

            Widgets.HelpMarker("For a static somebody has already put on the server. You start with " +
                               "read access unless an admin has said otherwise.");

            ImGui.SameLine();

            if (ImGui.Button("Create on the server"))
            {
                sync.Create(profile, syncUrl.Trim(), syncName.Trim(), syncPassword, syncCharacter.Trim());
                syncPassword = string.Empty;
            }

            Widgets.HelpMarker("Puts this static on the server for the first time. You become its " +
                               "admin, and the password is what you hand to the rest of the group.");
        }

        if (!ready)
            Widgets.Coloured(Widgets.Muted, "Fill all four in.");
    }

    private static string LocalCharacter()
    {
        foreach (var player in new PartyReader().Read())
        {
            if (player.IsLocalPlayer)
                return player.Name;
        }

        return string.Empty;
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

            // The field has to be given the keyboard, and not until the frame after it exists.
            // Without this it is drawn unfocused, the check below sees an inactive field on the very
            // frame it appeared, and the rename cancels itself before anybody can type — which is
            // exactly what it looked like: an edit box that flashed and vanished.
            if (renameFresh)
            {
                ImGui.SetKeyboardFocusHere();
                renameFresh = false;
            }

            ImGui.InputText("##rename", ref renameBuffer, 48, ImGuiInputTextFlags.EnterReturnsTrue);

            // Committed however the field is left — Enter, Escape or a click elsewhere. Throwing a
            // rename away because the mouse moved is worse than keeping one somebody was still
            // typing, and Escape restores the original text anyway, so that case renames nothing.
            if (ImGui.IsItemDeactivated())
            {
                statics.Rename(profile, renameBuffer);
                renaming = string.Empty;
            }
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
            if (ImGui.SmallButton("Open") && statics.Switch(profile.Id))
                sync.Refresh();
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
            renameFresh = true;
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
    private void DrawSyncCell(StaticProfile profile)
    {
        switch (sync.StatusOf(profile))
        {
            case SyncStatus.Off:
                Widgets.Coloured(Widgets.Muted, "local only");
                Widgets.Tooltip("Nothing about this static leaves your machine.");
                return;

            case SyncStatus.NotJoined:
                Widgets.Coloured(Widgets.Wanted, "not joined");
                Widgets.Tooltip("Switched on, but this client has not claimed a token yet.");
                return;

            case SyncStatus.Working:
                Widgets.Coloured(Widgets.Wanted, "syncing…");
                return;

            case SyncStatus.Error:
                Widgets.Coloured(Widgets.Bad, "problem");
                Widgets.Tooltip(sync.Message);
                return;

            default:
                Widgets.Coloured(Widgets.Done, profile.Sync.Role.ToString().ToLowerInvariant());
                Widgets.Tooltip($"{profile.Sync.CharacterName} at {profile.Sync.Url}");
                return;
        }
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

        if (!config.CanWrite)
        {
            Widgets.ReadOnlyNotice("who is in this static is the admin's to change");
            ImGuiHelpers.ScaledDummy(4f);
        }

        using var gate = ImRaii.Disabled(!config.CanWrite);

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
