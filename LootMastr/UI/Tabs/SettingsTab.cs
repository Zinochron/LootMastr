using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr.UI.Tabs;

public sealed class SettingsTab : ITab
{
    private const string DragPayload = "LootMastrPriority";

    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly LootPlanner planner;

    /// <summary>Row currently being dragged; ImGui's payload only has to say that one is.</summary>
    private int dragIndex = -1;

    public SettingsTab(Configuration config, RosterStore roster, LootPlanner planner)
    {
        this.config = config;
        this.roster = roster;
        this.planner = planner;
    }

    public string Title => "Settings";
    public string Id => "settings";

    public void Draw()
    {
        ImGui.TextUnformatted("Distribution");
        ImGui.Separator();

        var lookahead = config.LookaheadWeeks;
        if (ImGui.SliderInt("Weeks to look ahead", ref lookahead, 1, 20))
        {
            config.LookaheadWeeks = lookahead;
            config.Save();
        }

        Widgets.HelpMarker("How far the planner simulates before judging an assignment. Higher is more " +
                           "accurate near the start of a tier and makes no difference near the end.");

        DrawWeights();

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.TextUnformatted("Automation");
        ImGui.Separator();

        DrawModeChoice();

        var announce = config.AnnounceInPartyChat;
        if (ImGui.Checkbox("Announce assignments in party chat", ref announce))
        {
            config.AnnounceInPartyChat = announce;
            config.Save();
        }

        Widgets.HelpMarker("Off by default, because it puts words in your mouth. When on, each " +
                           "assignment is posted to /p as it happens.");

        var delay = config.ActionDelayMs;
        if (ImGui.SliderInt("Delay between actions (ms)", ref delay, 100, 2000))
        {
            config.ActionDelayMs = delay;
            config.Save();
        }

        Widgets.HelpMarker("Spacing between two clicks in the loot window. Too low and a window that is " +
                           "still transitioning receives the same click twice.");

        var verbose = config.VerboseChat;
        if (ImGui.Checkbox("Log what the plugin does to chat", ref verbose))
        {
            config.VerboseChat = verbose;
            config.Save();
        }
    }

    /// <summary>
    /// The weights the ranking is built from. They are exposed because "damage dealers first" means
    /// something different in every static, and because a suggestion is only worth following if the
    /// rule behind it can be read.
    /// </summary>
    private void DrawWeights()
    {
        var rules = config.Rules;

        var last = (float)rules.LastFinisherWeight;
        if (ImGui.SliderFloat("Weight on the slowest player", ref last, 0f, 3f, "%.2f"))
        {
            rules.LastFinisherWeight = last;
            config.Save();
        }

        Widgets.HelpMarker("At 1.00 and above the plan optimises for the last person to finish, which " +
                           "is usually what a static wants. Lowering it lets the plan trade a late tank " +
                           "for two early damage dealers.");

        var dps = (float)rules.DpsWeight;
        if (ImGui.SliderFloat("Damage dealer priority", ref dps, 1f, 3f, "%.2f"))
        {
            rules.DpsWeight = dps;
            config.Save();
        }

        Widgets.HelpMarker("How much a damage dealer finishing late counts against a plan, next to a " +
                           "tank or healer. At 1.00 the roles are equal. This only ever settles a " +
                           "choice that would otherwise be a coin flip — it cannot make the group as a " +
                           "whole finish later.");

        var fairness = (float)rules.FairnessWeight;
        if (ImGui.SliderFloat("Spread the loot around", ref fairness, 0f, 0.5f, "%.3f"))
        {
            rules.FairnessWeight = fairness;
            config.Save();
        }

        Widgets.HelpMarker("Weeks of simulated delay one already-won item is worth. Kept small on " +
                           "purpose: it breaks ties between equal candidates rather than overriding " +
                           "who actually needs the piece more.");

        ImGuiHelpers.ScaledDummy(6f);
        DrawForecastLine();

        ImGuiHelpers.ScaledDummy(6f);
        DrawPriorityOrder();
    }

    /// <summary>
    /// What the current weights actually produce, right here. Sliders with no visible consequence
    /// are guesswork — this is the one number they are all in aid of.
    /// </summary>
    private void DrawForecastLine()
    {
        if (roster.Members.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Add players to see what these settings produce.");
            return;
        }

        var result = planner.Forecast();

        if (result.BeyondHorizon(result.LastFinishWeek))
            Widgets.Coloured(Widgets.Bad, $"Not everyone is geared within {result.Horizon} weeks.");
        else if (result.LastFinishWeek == 0)
            Widgets.Coloured(Widgets.Done, "Everyone is already geared.");
        else
            Widgets.Coloured(Widgets.Done, $"The group is geared after {result.LastFinishWeek} week(s).");

        ImGui.SameLine();
        ImGui.TextDisabled("— full breakdown on the Plan tab");
    }

    /// <summary>
    /// Roster order is the final tiebreak when two players are equal candidates, so it is a real
    /// setting rather than just how the table happens to be sorted. Drag to reorder.
    /// </summary>
    private void DrawPriorityOrder()
    {
        ImGui.TextUnformatted("Priority order");
        Widgets.HelpMarker("Used only when two players come out exactly equal — after the effect on " +
                           "the group's finish week, after role, after who has won least. Drag a name " +
                           "to move it.");

        if (roster.Members.Count == 0)
            return;

        var members = roster.Members;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            using var id = ImRaii.PushId(member.Key);

            var role = roster.RoleOf(member);
            ImGui.Selectable($"{i + 1}.  {member.Name}   ({role})");

            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoPreviewTooltip))
            {
                dragIndex = i;
                ImGui.SetDragDropPayload(DragPayload, ReadOnlySpan<byte>.Empty);
                ImGui.TextUnformatted(member.Name);
                ImGui.EndDragDropSource();
            }

            if (!ImGui.BeginDragDropTarget())
                continue;

            unsafe
            {
                if (!ImGui.AcceptDragDropPayload(DragPayload).IsNull && dragIndex >= 0 && dragIndex != i)
                {
                    var moved = members[dragIndex];
                    members.RemoveAt(dragIndex);
                    members.Insert(i, moved);
                    config.Save();
                    dragIndex = -1;
                }
            }

            ImGui.EndDragDropTarget();
        }
    }

    private void DrawModeChoice()
    {
        foreach (var (mode, label, help) in Modes)
        {
            if (ImGui.RadioButton(label, config.Mode == mode))
            {
                config.Mode = mode;
                config.Save();
            }

            Widgets.HelpMarker(help);
        }
    }

    private static readonly (AssignmentMode Mode, string Label, string Help)[] Modes =
    [
        (AssignmentMode.SuggestOnly, "Suggest only",
            "Rank the candidates and stop there. The plugin never touches the loot window."),
        (AssignmentMode.Confirm, "Ask before assigning",
            "Show who should get the item and wait for a button press before assigning it."),
        (AssignmentMode.Automatic, "Assign automatically",
            "Hand the item to the planned player without asking. Requires you to be party leader " +
            "with the Lootmaster rule active."),
    ];

    public void Dispose() { }
}
