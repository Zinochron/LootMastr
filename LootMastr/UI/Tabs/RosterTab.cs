using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Automation;
using LootMastr.Data;
using LootMastr.Import;
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
    private readonly ClearTracker clears;

    private string newName = string.Empty;
    private string newWorld = string.Empty;
    private string urlBuffer = string.Empty;
    private string urlBufferFor = string.Empty;

    public RosterTab(Configuration config, RosterStore roster, JobCatalog jobs, PartyReader party,
                     BisImporter importer, TierCatalog tiers, ClearTracker clears)
    {
        this.config = config;
        this.roster = roster;
        this.jobs = jobs;
        this.party = party;
        this.importer = importer;
        this.tiers = tiers;
        this.clears = clears;
    }

    public string Title => "Roster";
    public string Id => "roster";

    public void Draw()
    {
        importer.Poll();

        DrawToolbar();
        DrawClearPrompt();
        DrawImportStatus();
        ImGuiHelpers.ScaledDummy(4f);

        if (roster.Members.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted,
                             "No one in the roster yet. Add players by hand, or press \"Sync from party\" while the static is grouped up.");
            return;
        }

        DrawLegend();
        DrawGrid();
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

    /// <summary>
    /// Counting books is a confirmation rather than something that happens on its own — a book
    /// counted twice quietly bends every forecast after it.
    /// </summary>
    private void DrawClearPrompt()
    {
        if (clears.Pending == null)
        {
            if (!string.IsNullOrEmpty(clears.Status))
            {
                ImGuiHelpers.ScaledDummy(2f);
                Widgets.Coloured(Widgets.Muted, clears.Status);
            }

            return;
        }

        ImGuiHelpers.ScaledDummy(4f);
        Widgets.Coloured(Widgets.Wanted,
                         $"{clears.Pending.Name} cleared — add a book for {clears.PendingPlayers.Length} player(s)?");

        if (ImGui.Button("Add books"))
            clears.Confirm();

        ImGui.SameLine();
        if (ImGui.Button("Not now"))
            clears.Dismiss();
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

    private static void DrawLegend()
    {
        Widgets.Coloured(Widgets.Wanted, "Raid");
        ImGui.SameLine(0f, 4f);
        ImGui.TextDisabled("needs a coffer");

        ImGui.SameLine(0f, 12f);
        Widgets.Coloured(Widgets.Augment, "Tome+");
        ImGui.SameLine(0f, 4f);
        ImGui.TextDisabled("needs an upgrade material");

        ImGui.SameLine(0f, 12f);
        Widgets.Coloured(Widgets.Done, "✓");
        ImGui.SameLine(0f, 4f);
        ImGui.TextDisabled("done");

        ImGui.SameLine(0f, 12f);
        Widgets.Coloured(Widgets.Muted, "Tome / Craft / —");
        ImGui.SameLine(0f, 4f);
        ImGui.TextDisabled("costs the raid nothing");
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
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(member.Name);

        var role = roster.RoleOf(member);
        var summary = SummaryOf(member);

        Widgets.Tooltip($"{member.DisplayName}\n{job.Name} ({role})\n\n{summary}");

        if (role == RaidRole.Dps)
        {
            ImGui.SameLine();
            Widgets.Coloured(Widgets.Wanted, "*");
            Widgets.Tooltip("Damage dealer — gets priority on equal footing.");
        }
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
        var satisfied = need.IsSatisfied;
        var label = need.Source.Label();

        // Done pieces keep their label and gain a tick rather than being replaced by one: which
        // source a finished slot came from is still the interesting part when reading a row, and
        // the tick carries "done" on its own for anyone who cannot separate the green from the
        // orange.
        if (need.Source.NeedsRaidResource() && satisfied)
            label = $"{label} ✓";

        using (ImRaii.PushColor(ImGuiCol.Text, Widgets.ColourFor(need.Source, satisfied)))
        {
            if (ImGui.SmallButton($"{label}##{slot}"))
                ImGui.OpenPopup($"##need{slot}");
        }

        Widgets.Tooltip($"{member.Name} — {slot.Label()}\n{need.Source.Label()}: {need.Source.Description()}" +
                        (need.BisItemId != 0 ? "\n\nFrom the imported gear set." : string.Empty));

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

    private static string SideLabel(GearSide side) => side switch
    {
        GearSide.Weapon => "weapon upgrade",
        GearSide.Left => "armour upgrade",
        _ => "accessory upgrade",
    };

    public void Dispose() { }
}
