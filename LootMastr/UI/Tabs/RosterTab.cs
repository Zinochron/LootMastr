using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Automation;
using LootMastr.Data;
using LootMastr.Import;
using LootMastr.Planning;
using LootMastr.Planning.Dps;
using LootMastr.Roster;

namespace LootMastr.UI.Tabs;

/// <summary>
/// The static and what everyone still needs. This is the one tab that is useful on its own:
/// filling the grid in by hand already gives a shared, checkable list.
/// </summary>
public sealed class RosterTab : ITab
{
    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly JobCatalog jobs;
    private readonly PartyReader party;
    private readonly BisImporter importer;
    private readonly TierCatalog tiers;
    private readonly GearScanner scanner;
    private readonly ItemCatalog items;
    private readonly GearComparer gear;
    private readonly LootPlanner planner;

    /// <summary>Whose preview is open, and the plan it was built from. Empty means none.</summary>
    private string previewFor = string.Empty;

    private string newName = string.Empty;
    private string newWorld = string.Empty;
    private string urlBuffer = string.Empty;
    private string urlBufferFor = string.Empty;

    public RosterTab(Configuration config, RosterStore roster, JobCatalog jobs, PartyReader party,
                     BisImporter importer, TierCatalog tiers, GearScanner scanner,
                     ItemCatalog items, GearComparer gear, LootPlanner planner)
    {
        this.config = config;
        this.roster = roster;
        this.jobs = jobs;
        this.party = party;
        this.importer = importer;
        this.tiers = tiers;
        this.scanner = scanner;
        this.items = items;
        this.gear = gear;
        this.planner = planner;
    }

    public string Title => "Roster";
    public string Id => "roster";

    public void Draw()
    {
        importer.Poll();

        DrawToolbar();
        DrawImportStatus();

        if (!string.IsNullOrEmpty(scanner.Status))
            Widgets.Coloured(scanner.IsRunning ? Widgets.Wanted : Widgets.Muted, scanner.Status);
        ImGuiHelpers.ScaledDummy(4f);

        if (roster.Members.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted,
                             "No one in the roster yet. Add players by hand, or press \"Sync from party\" while the static is grouped up.");
            return;
        }

        if (config.ExpertMode)
        {
            DrawPlayerList();
            ImGuiHelpers.ScaledDummy(4f);
            DrawPlayerSheets();
            return;
        }

        DrawLegend();
        DrawGrid();
    }

    /// <summary>
    /// Who is in the static, and nothing about their gear.
    ///
    /// Folded away by default. In expert mode this is the part you touch when somebody joins or the
    /// order changes, which is rarely; the sheets below are what you actually read. The same three
    /// cell drawers as the simple grid, so a link or a book count behaves identically in both.
    /// </summary>
    private void DrawPlayerList()
    {
        if (!ImGui.CollapsingHeader($"Players ({roster.Members.Count})###players"))
            return;

        using var table = ImRaii.Table("##rosterList", 4,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("BiS", ImGuiTableColumnFlags.WidthFixed, 46f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Books", ImGuiTableColumnFlags.WidthFixed, 74f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 62f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        RosterMember? removing = null;

        foreach (var member in roster.Members.ToList())
        {
            using var id = ImRaii.PushId(member.Key);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawPlayerCell(member);

            ImGui.TableNextColumn();
            DrawImportCell(member);

            ImGui.TableNextColumn();
            DrawTokenCell(member);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("^"))
                roster.Move(member, -1);

            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("v"))
                roster.Move(member, 1);

            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
                removing = member;

            Widgets.Tooltip("Ctrl+click to remove this player from the roster.");
        }

        if (removing != null)
            roster.Remove(removing);
    }

    /// <summary>One tab per player, because eleven slots × two sides does not fit a shared grid.</summary>
    private void DrawPlayerSheets()
    {
        using var tabs = ImRaii.TabBar("##sheets", ImGuiTabBarFlags.FittingPolicyScroll);
        if (!tabs.Success)
            return;

        foreach (var member in roster.Members.ToList())
        {
            // Labelled by name, identified by key: renaming a player must not reset their tab.
            using var tab = ImRaii.TabItem($"{member.Name}###sheet{member.Key}");
            if (!tab.Success)
                continue;

            using var id = ImRaii.PushId(member.Key);
            DrawSheet(member);
        }
    }

    /// <summary>
    /// One player: what they are wearing on the left, what they are aiming at on the right.
    ///
    /// Two panes rather than a table with an Is and an Ought column. Item names run to forty
    /// characters and a table would either wrap them or cut them, and reading down one side is what
    /// this is for — "what is still wrong with this set" is a question about one column at a time.
    /// </summary>
    private void DrawSheet(RosterMember member)
    {
        var job = jobs.Get(member.JobId);
        var role = roster.RoleOf(member);

        Widgets.Icon(job.IconId, 20f);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{member.DisplayName} — {job.Name} ({role})");

        if (member.HasBeenScanned)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"i{member.AverageItemLevel}, read {Ago(member.LastScannedUtc)}");
        }
        else
        {
            ImGui.SameLine();
            Widgets.Coloured(Widgets.Wanted, "gear not read yet");
            Widgets.Tooltip("Read it below, or let it happen on its own when the group next enters " +
                            "a duty. Until then the left column is empty and nothing can be compared.");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(scanner.IsRunning))
        {
            if (ImGui.SmallButton("Read this player"))
                Services.Chat.Print($"LootMastr: {scanner.StartFor(member)}");
        }

        Widgets.Tooltip("Examines this one character. They have to be in the party and in the zone.");

        ImGui.TextDisabled(SummaryOf(member));
        DrawPreview(member);

        if (!string.IsNullOrEmpty(member.ImportWarning))
            Widgets.Coloured(Widgets.Wanted, $"? {member.ImportWarning}");

        ImGuiHelpers.ScaledDummy(4f);

        var width = (ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().ItemSpacing.X * 2)) / 2f;

        using (var left = ImRaii.Child("##current", new Vector2(width, 0f), true))
        {
            if (left.Success)
                DrawCurrentColumn(member);
        }

        ImGui.SameLine();

        using var right = ImRaii.Child("##target", new Vector2(width, 0f), true);
        if (right.Success)
            DrawTargetColumn(member);
    }

    /// <summary>
    /// What a set is worth, at the top of its own column.
    ///
    /// One of these over each pane, so the two are read the way the panes are: this is what they do,
    /// that is what they would do. Estimated DPS is the headline because nobody thinks in potency;
    /// damage per 100 potency is the exact half and sits in the tooltip, along with the recast.
    /// </summary>
    private static void DrawEstimateLine(DamageEstimate estimate, string what, double? gainOverNow)
    {
        Widgets.Coloured(Widgets.Done, $"~{estimate.EstimatedDps:N0} dps");

        if (gainOverNow is { } gain && Math.Abs(gain) >= 1)
        {
            ImGui.SameLine(0f, 4f);
            Widgets.Coloured(gain > 0 ? Widgets.Wanted : Widgets.Muted, $"({gain:+#,##0;-#,##0})");
        }

        Widgets.Tooltip(
            $"{what}\n\n" +
            $"{estimate.DamagePer100Potency:N0} damage per 100 potency — exact, straight out of the stats.\n" +
            $"{estimate.Gcd:0.00} second global cooldown.\n\n" +
            $"~{estimate.EstimatedDps:N0} dps converts that with a rotation profile.\n" +
            (estimate.Caveat ?? "That profile has been checked against a gear planner."));
    }

    /// <summary>
    /// What the next planned pieces would do for this player.
    ///
    /// Behind a button rather than always on: it costs a plan and an estimate per drop, and it is a
    /// question you ask once before a raid night rather than every frame the tab is open.
    /// </summary>
    private void DrawPreview(RosterMember member)
    {
        var open = previewFor == member.Key;

        ImGui.SameLine();
        if (ImGui.SmallButton(open ? "Hide what is coming" : "What is coming"))
            previewFor = open ? string.Empty : member.Key;

        Widgets.Tooltip("The pieces this week's plan has for them, and what each one is worth.");

        if (!open)
            return;

        var coming = planner.ComingWeek().Awards
                            .Where(a => a.PlayerKey == member.Key)
                            .ToList();

        using var indent = ImRaii.PushIndent();

        if (coming.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing planned for them this week.");
            return;
        }

        foreach (var award in coming)
        {
            var slot = award.Slot;
            if (slot == null)
                continue;

            var target = member.NeedFor(slot.Value).BisItemId;
            var gain = target == 0 ? null : gear.Gain(member, slot.Value, target);

            ImGui.TextUnformatted($"{award.What}{(award.Bought ? " (books)" : string.Empty)}");
            ImGui.SameLine();

            if (gain is not { } change)
            {
                Widgets.Coloured(Widgets.Muted, "— nothing to compare it against");
                continue;
            }

            var colour = change.IsUpgrade ? Widgets.Done : Widgets.Muted;
            Widgets.Coloured(colour, $"{change.Percent:+0.00;-0.00;0.00}%");

            ImGui.SameLine();
            ImGui.TextDisabled($"{change.Before.EstimatedDps:N0} → {change.After.EstimatedDps:N0} dps" +
                               (Math.Abs(change.After.Gcd - change.Before.Gcd) > 0.001
                                    ? $", GCD {change.Before.Gcd:0.00} → {change.After.Gcd:0.00}"
                                    : string.Empty));

            Widgets.Tooltip($"{items.GetItemName(target)}\n\n" +
                            $"{change.Before.DamagePer100Potency:N0} → " +
                            $"{change.After.DamagePer100Potency:N0} per 100 potency.\n\n" +
                            "Counted on the item's own stats. Whatever is melded into the piece they " +
                            "are wearing now is assumed to carry over, so a piece with more meld " +
                            "slots than the old one is worth a little more than this says.");
        }
    }

    private void DrawCurrentColumn(RosterMember member)
    {
        ImGui.TextUnformatted("Wearing");
        Widgets.HelpMarker("Read from the game — real items, not glamours. Examine reports what a " +
                           "character actually has on.");

        if (gear.Estimate(member) is { } now)
        {
            DrawEstimateLine(now, "What they do on the set they are wearing. Measured off the " +
                                  "character, so materia and food are already in it.", null);
        }
        else
        {
            Widgets.Coloured(Widgets.Muted, "no estimate yet");
            Widgets.Tooltip("Needs their gear read, and a weapon in the list.");
        }

        ImGui.Separator();

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            SlotLabel(slot);

            if (!member.HasBeenScanned)
            {
                Widgets.Coloured(Widgets.Muted, "not read yet");
                continue;
            }

            if (need.EquippedItemId == 0)
            {
                Widgets.Coloured(Widgets.Muted, "empty");
                continue;
            }

            var item = items.GetItem(need.EquippedItemId);
            var wearing = need.IsWearingTarget;

            Widgets.Icon(item.IconId, 18f);
            ImGui.SameLine(0f, 4f);
            Widgets.Coloured(wearing ? Widgets.Done : Widgets.Muted,
                             $"{item.Name}{(wearing ? " ✓" : string.Empty)}");

            Widgets.Tooltip($"{item.Name}\ni{item.ItemLevel} — {need.EquippedSource.Label()}\n\n" +
                            (wearing ? "This is the target piece." : "Not the target piece."));
        }
    }

    private void DrawTargetColumn(RosterMember member)
    {
        ImGui.TextUnformatted("Aiming at");
        Widgets.HelpMarker("From the imported set, or set by hand. Click a row to change what the " +
                           "slot wants or to tick it off.");

        // The whole point of the column, at the top of it: not what the next piece is worth, but
        // where the set ends up. The number beside it is the distance still to go.
        if (gear.TargetGain(member) is { } target)
        {
            DrawEstimateLine(target.After,
                             "What they would do with every target piece — the finish line.\n\n" +
                             "Counted on the items' own stats, with the melds they are wearing now " +
                             "assumed to carry over. A target set with more meld slots than the " +
                             "current one is worth a little more than this says.",
                             target.Dps);
        }
        else
        {
            Widgets.Coloured(Widgets.Muted, "no estimate yet");
            Widgets.Tooltip("Needs their gear read and a target set with items in it.");
        }

        ImGui.Separator();

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);
            var state = need.StateFor(member.HasBeenScanned);

            SlotLabel(slot);

            var colour = Widgets.ColourFor(need.Source, state);
            var mark = Widgets.MarkFor(state);

            if (need.BisItemId != 0)
            {
                var item = items.GetItem(need.BisItemId);
                var gain = gear.GainOfTarget(member, slot);

                Widgets.Icon(item.IconId, 18f);
                ImGui.SameLine(0f, 4f);

                // The gain goes in the label rather than after it, so it cannot be pushed off the
                // edge of the pane by a long item name — which most of these are.
                var suffix = gain is { } change && Math.Abs(change.Percent) >= 0.005
                                 ? $"   {change.Percent:+0.00;-0.00}%"
                                 : string.Empty;

                using (ImRaii.PushColor(ImGuiCol.Text, colour))
                {
                    if (ImGui.Selectable($"{item.Name}{mark}{suffix}##{slot}"))
                        ImGui.OpenPopup($"##need{slot}");
                }

                Widgets.Tooltip(DescribeCell(member, slot, need, state) + GainNote(gain));
            }
            else
            {
                // No exact item — the slot was set by hand, so the source word is all there is.
                using (ImRaii.PushColor(ImGuiCol.Text, colour))
                {
                    if (ImGui.Selectable($"{need.Source.Label()}{mark}##{slot}"))
                        ImGui.OpenPopup($"##need{slot}");
                }

                Widgets.Tooltip(DescribeCell(member, slot, need, state));
            }

            DrawNeedPopup(member, slot, need);
        }
    }

    /// <summary>
    /// The gain, spelled out for the tooltip.
    ///
    /// The one assumption in it is stated rather than left implicit: the melds on the piece they are
    /// wearing are taken to carry over, because what would go into a piece nobody owns is not
    /// knowable. That errs low on an upgrade with more meld slots, and saying so is the difference
    /// between an estimate and a claim.
    /// </summary>
    private static string GainNote(GearGain? gain)
    {
        if (gain is not { } change)
            return string.Empty;

        // The flat number as well as the share, because the plan ranks on the flat one: a percentage
        // of one player's output is not comparable with the same percentage of another's.
        var lines = $"\n\n{change.Before.EstimatedDps:N0} → {change.After.EstimatedDps:N0} dps " +
                    $"({change.Dps:+#,##0;-#,##0} dps, {change.Percent:+0.00;-0.00;0.00}%)\n" +
                    $"{change.Before.DamagePer100Potency:N0} → {change.After.DamagePer100Potency:N0} " +
                    "per 100 potency";

        if (Math.Abs(change.After.Gcd - change.Before.Gcd) > 0.001)
            lines += $"\nGCD {change.Before.Gcd:0.00} → {change.After.Gcd:0.00}";

        return lines + "\n\nCounted on the items' own stats. The melds on the piece they are wearing " +
               "now are assumed to carry over.";
    }

    /// <summary>The slot's name at a fixed width, so both columns line up without being a table.</summary>
    private static void SlotLabel(GearSlot slot)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(slot.ShortLabel());
        ImGui.SameLine(66f * ImGuiHelpers.GlobalScale);
    }

    private static string Ago(DateTime? at)
    {
        if (at == null)
            return "never";

        var span = DateTime.UtcNow - at.Value;

        return span.TotalMinutes < 1 ? "just now"
             : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
             : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
             : $"{(int)span.TotalDays} d ago";
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("Sync from party"))
        {
            var added = roster.SyncFromParty(party.Read());
            Services.Chat.Print(added > 0
                                    ? $"LootMastr: added {added} player(s) to the roster."
                                    : "LootMastr: roster already had everyone in the party.");
        }

        Widgets.HelpMarker("Adds anyone in your party who is not in the roster yet, and refreshes " +
                           "the job of everyone who already is. Nothing is ever removed.");

        ImGui.SameLine();

        if (scanner.IsRunning)
        {
            if (ImGui.Button("Stop reading"))
                scanner.Stop("Stopped.");
        }
        else if (ImGui.Button("Read gear"))
        {
            Services.Chat.Print($"LootMastr: {scanner.Start()}");
        }

        Widgets.HelpMarker("Reads your own equipment, then examines each party member in turn to read " +
                           "theirs. Examine reports real items, not glamours.\n\n" +
                           "Anyone in another zone is skipped. Nothing is ever un-ticked by this: " +
                           "a piece that was handed over stays handed over whether it is worn or not.");

        ImGui.SameLine();
        if (ImGui.Button("Re-file imports"))
            importer.Reclassify();

        Widgets.HelpMarker("Runs the raid / tome / augmented decision over the sets already imported, " +
                           "without fetching them again. Worth pressing after discovering the tier or " +
                           "correcting how augmented gear is spelled.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##newName", "Name", ref newName, 32);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
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

    private void DrawImportStatus()
    {
        if (importer.Choosing != null)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextUnformatted($"Which set is {importer.Choosing.Name}'s BiS?");

            foreach (var set in importer.Choices)
            {
                if (ImGui.Selectable($"{set.Name}  ({set.Items.Count} pieces)"))
                {
                    importer.Apply(importer.Choosing, set);
                    break;
                }
            }

            if (ImGui.SmallButton("Cancel"))
                importer.Cancel();

            return;
        }

        if (string.IsNullOrEmpty(importer.Status))
            return;

        ImGuiHelpers.ScaledDummy(2f);
        Widgets.Coloured(Widgets.Muted, importer.Status);
    }

    /// <summary>
    /// The word in a cell is what the player wants, the mark after it is what they actually have.
    /// </summary>
    private static void DrawLegend()
    {
        ImGui.TextDisabled("wants:");
        ImGui.SameLine(0f, 4f);
        Widgets.Coloured(Widgets.Wanted, "Raid");
        ImGui.SameLine(0f, 6f);
        Widgets.Coloured(Widgets.Augment, "Tome+");
        ImGui.SameLine(0f, 6f);
        Widgets.Coloured(Widgets.Muted, "Tome / Craft / —");

        ImGui.SameLine(0f, 16f);
        ImGui.TextDisabled("has:");
        ImGui.SameLine(0f, 4f);
        Widgets.Coloured(Widgets.Done, "✓ worn");
        ImGui.SameLine(0f, 6f);
        Widgets.Coloured(Widgets.NotWorn, "✓! given, not worn");

        Widgets.HelpMarker("A slot marked \"given, not worn\" has already been handed over — usually " +
                           "an unopened coffer. It never comes up for assignment again; the mark is " +
                           "there so the player knows they still have something to do.\n\n" +
                           "\"has\" only appears once that character's gear has been read.");
    }

    private void DrawGrid()
    {
        // Player, BiS, Books and the reorder buttons, then one column per slot. Getting this count
        // wrong does not fail loudly — ImGui just drops the overflow, which ate the Ring 2 column.
        var columns = 4 + Slots.All.Length;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.SizingFixedFit;

        using var table = ImRaii.Table("##rosterGrid", columns, flags);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 190f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("BiS", ImGuiTableColumnFlags.WidthFixed, 46f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Books", ImGuiTableColumnFlags.WidthFixed, 74f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 62f * ImGuiHelpers.GlobalScale);

        foreach (var slot in Slots.All)
            ImGui.TableSetupColumn(slot.ShortLabel(), ImGuiTableColumnFlags.WidthFixed, 72f * ImGuiHelpers.GlobalScale);

        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableHeadersRow();

        RosterMember? removing = null;

        foreach (var member in roster.Members.ToList())
        {
            using var id = ImRaii.PushId(member.Key);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawPlayerCell(member);

            ImGui.TableNextColumn();
            DrawImportCell(member);

            ImGui.TableNextColumn();
            DrawTokenCell(member);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("^"))
                roster.Move(member, -1);

            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("v"))
                roster.Move(member, 1);

            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
                removing = member;

            Widgets.Tooltip("Ctrl+click to remove this player from the roster.");

            foreach (var slot in Slots.All)
            {
                ImGui.TableNextColumn();
                DrawNeedCell(member, slot);
            }
        }

        if (removing != null)
            roster.Remove(removing);
    }

    private void DrawPlayerCell(RosterMember member)
    {
        var job = jobs.Get(member.JobId);

        Widgets.Icon(job.IconId, 18f);

        if (ImGui.IsItemClicked())
            ImGui.OpenPopup("##job");

        var role = roster.RoleOf(member);
        Widgets.Tooltip($"{job.Name} ({role})\n\nClick to change job.");

        DrawJobPicker(member);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(member.Name);

        Widgets.Tooltip($"{member.DisplayName}\n{job.Name} ({role})\n\n{SummaryOf(member)}");

        if (role == RaidRole.Dps)
        {
            ImGui.SameLine();
            Widgets.Coloured(Widgets.Wanted, "*");
            Widgets.Tooltip("Damage dealer — gets priority on equal footing.");
        }

        if (string.IsNullOrEmpty(member.ImportWarning))
            return;

        ImGui.SameLine();
        Widgets.Coloured(Widgets.Wanted, "?");
        Widgets.Tooltip(member.ImportWarning);
    }

    private void DrawImportCell(RosterMember member)
    {
        var linked = !string.IsNullOrWhiteSpace(member.GearPlannerUrl);

        using (ImRaii.PushColor(ImGuiCol.Text, linked ? Widgets.Done : Widgets.Muted))
        {
            if (ImGui.SmallButton(linked ? "link" : "set"))
            {
                urlBuffer = member.GearPlannerUrl;
                urlBufferFor = member.Key;
                ImGui.OpenPopup("##bis");
            }
        }

        Widgets.Tooltip(linked
                            ? $"Imported from:\n{member.GearPlannerUrl}"
                            : "No gear set linked. Click to paste an XIVGear or Etro link.");

        using var popup = ImRaii.Popup("##bis");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted($"{member.Name} — gear set");
        ImGui.Separator();

        // One shared buffer, claimed by whichever row opened the popup, so two open popups cannot
        // overwrite each other's text.
        if (urlBufferFor != member.Key)
        {
            urlBuffer = member.GearPlannerUrl;
            urlBufferFor = member.Key;
        }

        ImGui.SetNextItemWidth(420f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##url", "https://xivgear.app/?page=sl|… or https://etro.gg/gearset/…",
                                ref urlBuffer, 512);

        using (ImRaii.Disabled(importer.IsBusy || string.IsNullOrWhiteSpace(urlBuffer)))
        {
            if (ImGui.Button("Import"))
            {
                importer.Start(member, urlBuffer);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!linked || importer.IsBusy))
        {
            if (ImGui.Button("Refresh"))
            {
                importer.Start(member, member.GearPlannerUrl);
                ImGui.CloseCurrentPopup();
            }
        }

        Widgets.HelpMarker("Re-reads the linked set. Slots you ticked off by hand keep their ticks; " +
                           "only what each slot wants is rewritten.");
    }

    /// <summary>
    /// Books held per fight. They matter as much as drops — a player two books short of buying a
    /// slot outright should not be competing for the coffer — so they are editable everywhere,
    /// not only where the game happens to count them.
    /// </summary>
    private void DrawTokenCell(RosterMember member)
    {
        var total = Enumerable.Range(1, 4).Sum(member.TokensFor);

        using (ImRaii.PushColor(ImGuiCol.Text, total > 0 ? Widgets.Done : Widgets.Muted))
        {
            if (ImGui.SmallButton($"{total}##books"))
                ImGui.OpenPopup("##booksPopup");
        }

        Widgets.Tooltip(string.Join("\n", tiers.Tier.Encounters
                                               .OrderBy(e => e.Index)
                                               .Select(e => $"{e.Name}: {member.TokensFor(e.Index)}")));

        using var popup = ImRaii.Popup("##booksPopup");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted($"{member.Name} — books held");
        ImGui.Separator();

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            var held = member.TokensFor(encounter.Index);

            ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
            if (!ImGui.InputInt(encounter.Name, ref held))
                continue;

            member.Tokens[encounter.Index] = Math.Max(0, held);
            config.Save();
        }
    }

    /// <summary>
    /// Changing someone's job by hand. Jobs are never pulled from the party on their own — a party
    /// picks up strangers and people swap for a pull — so this and "Sync from party" are the only
    /// two ways the roster's idea of a job moves.
    ///
    /// Everything downstream keys off the roster's fingerprint, which includes the job, so the plan
    /// and the schedule follow a change here without being told.
    /// </summary>
    private void DrawJobPicker(RosterMember member)
    {
        using var popup = ImRaii.Popup("##job");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted($"{member.Name} — job");
        ImGui.Separator();

        foreach (var group in jobs.All.Values
                                  .Where(j => j.Role != RaidRole.Unknown)
                                  .GroupBy(j => j.Role)
                                  .OrderBy(g => g.Key))
        {
            ImGui.TextDisabled(group.Key.ToString());

            foreach (var job in group.OrderBy(j => j.Abbreviation, StringComparer.Ordinal))
            {
                Widgets.Icon(job.IconId, 18f);
                ImGui.SameLine();

                if (!ImGui.Selectable($"{job.Abbreviation}  {job.Name}##{job.Id}", member.JobId == job.Id))
                    continue;

                roster.SetJob(member, job.Id);
            }
        }
    }

    private string SummaryOf(RosterMember member)
    {
        var open = Slots.All.Count(s => member.NeedFor(s).Source.NeedsRaidResource() && !member.NeedFor(s).IsSatisfied);
        var planned = Slots.All.Count(s => member.NeedFor(s).Source.NeedsRaidResource());

        return planned == 0
                   ? "Nothing planned from the raid yet."
                   : $"{planned - open}/{planned} raid pieces done.";
    }

    /// <summary>
    /// One cell of the grid. Clicking opens a popup rather than cycling in place: cycling through
    /// five sources by mis-clicking a 54 pixel button would silently rewrite the list.
    /// </summary>
    private void DrawNeedCell(RosterMember member, GearSlot slot)
    {
        var need = member.NeedFor(slot);
        var state = need.StateFor(member.HasBeenScanned);

        // The word is the target, the mark after it is the actual. Done pieces keep their label
        // and gain a tick rather than being replaced by one: which source a finished slot came
        // from is still the interesting part when reading a row, and the mark carries the state on
        // its own for anyone who cannot separate the colours.
        var label = need.Source.Label() + Widgets.MarkFor(state);

        using (ImRaii.PushColor(ImGuiCol.Text, Widgets.ColourFor(need.Source, state)))
        {
            if (ImGui.SmallButton($"{label}##{slot}"))
                ImGui.OpenPopup($"##need{slot}");
        }

        Widgets.Tooltip(DescribeCell(member, slot, need, state));

        DrawNeedPopup(member, slot, need);
    }

    /// <summary>
    /// What a slot wants, and whether it has been handed over. Shared by the grid cell and the
    /// expert sheet — the two views disagreeing about how a need is edited would be a good way to
    /// end up with two subtly different sets of rules.
    /// </summary>
    private void DrawNeedPopup(RosterMember member, GearSlot slot, SlotNeed need)
    {
        using var popup = ImRaii.Popup($"##need{slot}");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted($"{member.Name} — {slot.Label()}");
        ImGui.Separator();

        foreach (var source in Slots.SelectableSources())
        {
            if (ImGui.RadioButton(source.Label(), need.Source == source))
            {
                need.Source = source;
                config.Save();
            }

            Widgets.Tooltip(source.Description());
        }

        if (!need.Source.NeedsRaidResource())
            return;

        ImGui.Separator();

        if (need.Source == GearSource.Raid)
        {
            var obtained = need.Obtained;
            if (ImGui.Checkbox("Got the piece", ref obtained))
            {
                need.Obtained = obtained;
                config.Save();
            }
        }
        else
        {
            var upgraded = need.UpgradeObtained;
            var side = Slots.SideOf(slot);
            if (ImGui.Checkbox($"Got the {SideLabel(side)}", ref upgraded))
            {
                need.UpgradeObtained = upgraded;
                config.Save();
            }

            Widgets.HelpMarker("Only the upgrade material is tracked. The tomestone piece itself costs " +
                               "no raid resource, so it never competes for a drop.");
        }
    }

    /// <summary>Spells target and actual out in full, since the cell itself only has room for a word.</summary>
    private string DescribeCell(RosterMember member, GearSlot slot, SlotNeed need, SlotState state)
    {
        var lines = new List<string> { $"{member.Name} — {slot.Label()}", string.Empty };

        lines.Add($"Wants: {need.Source.Label()} — {need.Source.Description()}");

        if (need.BisItemId != 0)
            lines.Add($"       {items.GetItemName(need.BisItemId)}");

        if (!member.HasBeenScanned)
            lines.Add("Has:   not read yet — press \"Read gear\".");
        else if (need.EquippedItemId == 0)
            lines.Add("Has:   nothing equipped.");
        else
            lines.Add($"Has:   {items.GetItemName(need.EquippedItemId)} ({need.EquippedSource.Label()})");

        lines.Add(string.Empty);

        lines.Add(state switch
        {
            SlotState.NotPlanned => "Costs the raid nothing.",
            SlotState.Needed => "Still owed.",
            SlotState.Done => "Done.",
            SlotState.AssignedNotWorn =>
                "Handed over but not worn — the coffer is probably still unopened.\n" +
                "It stays out of the distribution either way; nobody gets it twice.",
            _ => string.Empty,
        });

        return string.Join("\n", lines);
    }

    private static string SideLabel(GearSide side) => side switch
    {
        GearSide.Weapon => "weapon upgrade",
        GearSide.Left => "armour upgrade",
        _ => "accessory upgrade",
    };

    public void Dispose() { }
}
