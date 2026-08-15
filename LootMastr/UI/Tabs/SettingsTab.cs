using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace LootMastr.UI.Tabs;

public sealed class SettingsTab : ITab
{
    private readonly Configuration config;

    public SettingsTab(Configuration config) => this.config = config;

    public string Title => "Settings";
    public string Id => "settings";

    public void Draw()
    {
        ImGui.TextUnformatted("Roster");
        ImGui.Separator();

        var autoSync = config.AutoSyncRosterFromParty;
        if (ImGui.Checkbox("Add party members to the roster automatically", ref autoSync))
        {
            config.AutoSyncRosterFromParty = autoSync;
            config.Save();
        }

        Widgets.HelpMarker("Anyone in your party who is not in the roster gets added the first time " +
                           "they are seen. Nothing is ever removed automatically.");

        ImGuiHelpers.ScaledDummy(10f);
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

        var last = (float)rules.LastFinisherWeight;
        if (ImGui.SliderFloat("Weight on the slowest player", ref last, 0f, 3f, "%.2f"))
        {
            rules.LastFinisherWeight = last;
            config.Save();
        }

        Widgets.HelpMarker("At 1.00 and above the plan optimises for the last person to finish, which " +
                           "is usually what a static wants. Lowering it lets the plan trade a late tank " +
                           "for two early damage dealers.");
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
