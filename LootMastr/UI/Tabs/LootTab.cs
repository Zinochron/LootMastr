using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Automation;

namespace LootMastr.UI.Tabs;

/// <summary>
/// The tab you have open during a raid: what is in the chest, who it should go to, and why.
/// </summary>
public sealed class LootTab : ITab
{
    private readonly Configuration config;
    private readonly LootAssigner assigner;
    private readonly ChatAnnouncer announcer;

    public LootTab(Configuration config, LootAssigner assigner, ChatAnnouncer announcer)
    {
        this.config = config;
        this.assigner = assigner;
        this.announcer = announcer;
    }

    public string Title => "Loot";
    public string Id => "loot";

    public void Draw()
    {
        assigner.Refresh();

        DrawBanner();

        if (assigner.Decisions.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No loot window open.");
            ImGui.TextDisabled("The Plan tab shows the same ranking ahead of time, per drop.");
            return;
        }

        ImGuiHelpers.ScaledDummy(4f);
        DrawDecisions();

        ImGuiHelpers.ScaledDummy(6f);
        DrawActions();
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

        using (ImRaii.Disabled(!verdict.Ok))
        {
            if (ImGui.Button("Assign all"))
            {
                foreach (var decision in assigner.Decisions.Where(d => d.Winner != null))
                {
                    if (!assigner.PerformAssignment(decision, out var reason))
                    {
                        Services.Chat.PrintError($"LootMastr: {reason}");
                        break;
                    }
                }
            }
        }

        Widgets.HelpMarker("Hands every item to its planned recipient through the game's loot " +
                           "recipient control.");

        ImGui.SameLine();

        if (ImGui.Button("Announce in /p"))
            announcer.Announce(assigner.Decisions);

        Widgets.HelpMarker("Posts one line naming who each item is for.");

        if (!string.IsNullOrEmpty(announcer.LastResult))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(announcer.LastResult);
        }

        if (!string.IsNullOrEmpty(assigner.Status))
            Widgets.Coloured(Widgets.Muted, assigner.Status);
    }

    public void Dispose() { }
}
