using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Automation;
using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr.UI.Tabs;

/// <summary>
/// The tab you have open during a raid: what is in the chest, who it should go to, and why.
/// </summary>
public sealed class LootTab : ITab
{
    private readonly Configuration config;
    private readonly LootAssigner assigner;
    private readonly ChatAnnouncer announcer;
    private readonly RosterStore roster;
    private readonly TierCatalog tiers;
    private readonly LootPlanner planner;
    private readonly ClearTracker clears;

    public LootTab(Configuration config, LootAssigner assigner, ChatAnnouncer announcer,
                   RosterStore roster, TierCatalog tiers, LootPlanner planner, ClearTracker clears)
    {
        this.config = config;
        this.assigner = assigner;
        this.announcer = announcer;
        this.roster = roster;
        this.tiers = tiers;
        this.planner = planner;
        this.clears = clears;
    }

    public string Title => "Loot";
    public string Id => "loot";

    public void Draw()
    {
        assigner.Refresh();

        DrawClearPrompt();
        DrawBanner();

        if (assigner.Decisions.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No loot window open.");
            ImGui.TextDisabled("The Plan tab shows the same ranking ahead of time, per drop.");
        }
        else
        {
            ImGuiHelpers.ScaledDummy(4f);
            DrawDecisions();

            ImGuiHelpers.ScaledDummy(6f);
            DrawActions();
        }

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(6f);
        DrawBooks();
    }

    /// <summary>
    /// Kills per fight and books per player, in one grid. These are the numbers the whole forecast
    /// rests on and the ones the game gives no way to read, so they have to be both visible and
    /// editable — a book counted wrong is invisible everywhere else.
    /// </summary>
    private void DrawBooks()
    {
        ImGui.TextUnformatted("Books");
        Widgets.HelpMarker("Every player who clears a fight gets one of its books that week. Kills are " +
                           "the group's count; the rows below are what each player is holding right now, " +
                           "after anything they have already spent.");
        ImGui.Separator();

        var encounters = tiers.Tier.Encounters.OrderBy(e => e.Index).ToList();
        if (encounters.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No fights defined — see the Tier tab.");
            return;
        }

        using var table = ImRaii.Table("##books", encounters.Count + 2,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);

        foreach (var encounter in encounters)
            ImGui.TableSetupColumn(encounter.Name, ImGuiTableColumnFlags.WidthFixed, 78f * ImGuiHelpers.GlobalScale);

        ImGui.TableSetupColumn("Can buy now", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        DrawKillsRow(encounters);

        foreach (var member in roster.Members)
            DrawPlayerBooks(member, encounters);
    }

    private void DrawKillsRow(IReadOnlyList<TierEncounter> encounters)
    {
        using var id = ImRaii.PushId("kills");

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Kills");
        Widgets.HelpMarker("How often the group has cleared each fight. Counted automatically when a " +
                           "clear is confirmed above, and editable here for anything that " +
                           "happened before the plugin was in use.");

        foreach (var encounter in encounters)
        {
            ImGui.TableNextColumn();

            using var column = ImRaii.PushId(encounter.Index);

            var kills = config.KillsFor(encounter.Index);
            ImGui.SetNextItemWidth(-1f);

            if (ImGui.InputInt("##kills", ref kills, 0))
            {
                config.Kills[encounter.Index] = Math.Max(0, kills);
                config.Save();
            }
        }

        ImGui.TableNextColumn();

        // The bulk action for a clear nobody confirmed in time, which is most of them.
        for (var i = 0; i < encounters.Count; i++)
        {
            var encounter = encounters[i];

            if (i > 0)
                ImGui.SameLine(0f, 4f);

            using var column = ImRaii.PushId(encounter.Index);

            if (ImGui.SmallButton($"+1 {encounter.Name}"))
                GiveBookToEveryone(encounter.Index);

            Widgets.Tooltip($"Counts one {encounter.Name} kill and gives every player in the roster one " +
                            "of its books.\n\nUse the prompt on the Roster tab instead when the clear " +
                            "just happened — that one only counts the people who were actually there.");
        }
    }

    private void GiveBookToEveryone(int encounter)
    {
        config.Kills[encounter] = config.KillsFor(encounter) + 1;

        foreach (var member in roster.Members)
            member.Tokens[encounter] = member.TokensFor(encounter) + 1;

        config.Save();
    }

    private void DrawPlayerBooks(RosterMember member, IReadOnlyList<TierEncounter> encounters)
    {
        using var id = ImRaii.PushId(member.Key);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(member.Name);

        foreach (var encounter in encounters)
        {
            ImGui.TableNextColumn();

            using var column = ImRaii.PushId(encounter.Index);

            var held = member.TokensFor(encounter.Index);
            var kills = config.KillsFor(encounter.Index);

            // Holding more than the group has killed is not possible and always means a typo.
            using var colour = ImRaii.PushColor(ImGuiCol.Text, Widgets.Bad, held > kills && kills > 0);

            ImGui.SetNextItemWidth(-1f);

            if (ImGui.InputInt("##books", ref held, 0))
            {
                member.Tokens[encounter.Index] = Math.Max(0, held);
                config.Save();
            }

            if (held > kills && kills > 0)
                Widgets.Tooltip($"More books than the group has {encounter.Name} kills ({kills}).");
        }

        ImGui.TableNextColumn();

        var affordable = planner.AffordableNow(member);

        if (affordable.Count == 0)
            ImGui.TextDisabled("—");
        else
            Widgets.Coloured(Widgets.Done, string.Join(", ", affordable));
    }

    /// <summary>
    /// Books earned by clearing, asked for rather than counted silently — a book counted twice
    /// quietly bends every forecast after it. It belongs here because this is the tab that is
    /// already open when a fight ends.
    /// </summary>
    private void DrawClearPrompt()
    {
        if (clears.Pending == null)
        {
            if (!string.IsNullOrEmpty(clears.Status))
                Widgets.Coloured(Widgets.Muted, clears.Status);

            return;
        }

        Widgets.Coloured(Widgets.Wanted,
                         $"{clears.Pending.Name} cleared — add a book for {clears.PendingPlayers.Length} player(s)?");

        if (ImGui.Button("Add books"))
            clears.Confirm();

        ImGui.SameLine();
        if (ImGui.Button("Not now"))
            clears.Dismiss();

        ImGuiHelpers.ScaledDummy(6f);
    }

    private void DrawBanner()
    {
        var verdict = assigner.Verdict;

        if (verdict.Ok)
        {
            Widgets.Coloured(Widgets.Done, "Lootmaster active — you can assign this chest.");
            return;
        }

        Widgets.Coloured(Widgets.Wanted, verdict.Reason);

        if (verdict.Reason.Contains("Lootmaster rule"))
        {
            Widgets.HelpMarker("Duty Finder > the wrench > Loot Rules > Lootmaster, set before the " +
                               "party enters. Without it every player rolls for themselves and the " +
                               "ranking below is a recommendation to call out rather than something " +
                               "that can be applied.");
        }
    }

    private void DrawDecisions()
    {
        using var table = ImRaii.Table("##decisions", 4,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Goes to", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Then", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var decision in assigner.Decisions)
        {
            using var id = ImRaii.PushId(decision.Item.Index);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.Icon(decision.Item.IconId, 18f);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(decision.Item.Name);

            if (decision.Item.WeeklyLootItem)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(weekly)");
            }

            ImGui.TableNextColumn();
            if (decision.Winner == null)
                Widgets.Coloured(Widgets.Muted, "—");
            else
                Widgets.Coloured(Widgets.Done, decision.Winner.Name);

            Widgets.Tooltip(decision.Reason);

            ImGui.TableNextColumn();
            DrawRunnersUp(decision);

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(decision.Winner == null))
            {
                if (ImGui.SmallButton("Record"))
                    assigner.ConfirmByHand(decision);
            }

            Widgets.Tooltip("Tick this off as received. Use it after assigning the item in the game " +
                            "window yourself — chat tracking usually does it on its own.");
        }
    }

    private static void DrawRunnersUp(LootDecision decision)
    {
        if (decision.Ranking.Count <= 1)
        {
            ImGui.TextDisabled(decision.Winner == null ? decision.Reason : "no one else needs it");
            return;
        }

        var rest = decision.Ranking.Skip(1).Take(3).Select(c => c.Member.Name);
        ImGui.TextDisabled(string.Join(" > ", rest));

        Widgets.Tooltip(string.Join("\n", decision.Ranking.Select((c, i) => $"{i + 1}. {c.Member.Name} — {c.Reason}")));
    }

    private void DrawActions()
    {
        var verdict = assigner.Verdict;

        // One at a time on purpose: each assignment walks three windows, and the next cannot start
        // until those are clear. Handing them over one by one also means a refusal stops there.
        var next = assigner.Decisions.FirstOrDefault(d => d.Winner != null && !d.Item.Decided);

        using (ImRaii.Disabled(!verdict.Ok || assigner.IsAssigning || next == null))
        {
            var label = next == null ? "Assign" : $"Assign {next.Item.What} to {next.Winner!.Name}";

            if (ImGui.Button(label) && next != null)
            {
                if (!assigner.PerformAssignment(next, out var reason))
                    Services.Chat.PrintError($"LootMastr: {reason}");
            }
        }

        Widgets.HelpMarker(config.Mode == AssignmentMode.Automatic
                               ? "Opens the assignment window, picks the player, and answers the " +
                                 "game's confirmation — but only once that confirmation names the " +
                                 "right player and the right item."
                               : "Opens the assignment window and picks the player, then leaves the " +
                                 "game's own \"Allow X to claim Y?\" for you to answer.\n\n" +
                                 "Set Automation to \"Assign automatically\" to have that answered too.");

        if (assigner.IsAssigning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                assigner.StopAssigning("Stopped.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Announce in /p"))
            announcer.Announce(assigner.Decisions);

        Widgets.HelpMarker("Posts one line naming who each item is for.");

        if (!string.IsNullOrEmpty(announcer.LastResult))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(announcer.LastResult);
        }

        if (!string.IsNullOrEmpty(assigner.RunnerStatus))
            Widgets.Coloured(assigner.IsAssigning ? Widgets.Wanted : Widgets.Muted, assigner.RunnerStatus);

        if (!string.IsNullOrEmpty(assigner.Status))
            Widgets.Coloured(Widgets.Muted, assigner.Status);
    }

    public void Dispose() { }
}
