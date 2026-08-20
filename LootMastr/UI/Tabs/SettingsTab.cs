using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Data;
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
        DrawReminders();

        ImGuiHelpers.ScaledDummy(10f);

        // Every setting on this tab belongs to the static, so the whole thing is either editable or
        // it is not. Nothing is hidden: what the group has decided is exactly what a member with
        // read access came here to find out.
        if (!config.CanWrite)
        {
            Widgets.ReadOnlyNotice("these are the group's settings");
            ImGuiHelpers.ScaledDummy(4f);
        }

        using var gate = ImRaii.Disabled(!config.CanWrite);

        DrawExpertMode();

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

        ImGuiHelpers.ScaledDummy(6f);
        DrawMountChoice();

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.TextUnformatted("Alt characters");
        ImGui.Separator();
        DrawAltCharacters();

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
    /// Whether the plugin works from exact gear or from what each slot is owed.
    ///
    /// Two radio buttons rather than a checkbox, because both settings are a position rather than
    /// one being the absence of the other: a static that keeps its lists by hand wants the simple
    /// one and is not missing anything.
    /// </summary>
    private void DrawExpertMode()
    {
        ImGui.TextUnformatted("Working mode");
        ImGui.Separator();

        var expert = config.ExpertMode;

        if (ImGui.RadioButton("Simple", !expert) && expert)
        {
            config.ExpertMode = false;
            config.Save();
        }

        Widgets.HelpMarker("A slot is a word and a tick — \"Raid, done\". Enough to run a " +
                           "distribution, and the version a group can keep up by hand.");

        ImGui.SameLine(0f, 20f);

        if (ImGui.RadioButton("Expert", expert) && !expert)
        {
            config.ExpertMode = true;
            config.Save();
        }

        Widgets.HelpMarker("Every slot carries the item actually equipped and the item aimed at. " +
                           "The roster becomes a plain list of players with a sheet each, rather " +
                           "than one grid of everything.\n\n" +
                           "This is what a damage estimate needs, and it is only maintainable " +
                           "because the gear scan fills the equipped side in on its own.");

        if (!expert)
            return;

        var auto = config.AutoReadGearOnEnter;
        if (ImGui.Checkbox("Read everyone's gear on entering a duty", ref auto))
        {
            config.AutoReadGearOnEnter = auto;
            config.Save();
        }

        Widgets.HelpMarker("Eight seconds after landing, and never in combat — if the pull has " +
                           "already started it waits it out.\n\n" +
                           "Only players in the roster are read, and only while their current job " +
                           "is the role the roster expects. Somebody on a damage job for a farm run " +
                           "is skipped and named rather than written onto their tank row.");

        if (!auto)
            Widgets.Coloured(Widgets.Muted, "The equipped side only updates when you press Read gear.");
    }

    /// <summary>
    /// The whole loot policy: which roles come first, which players come first, and how much of the
    /// loot to share out rather than funnel. Everything the plan shows follows from these three, so
    /// they are worth a paragraph of explanation each.
    /// </summary>
    private void DrawWeights()
    {
        DrawRoleOrder();

        ImGuiHelpers.ScaledDummy(6f);
        DrawSpread();

        ImGuiHelpers.ScaledDummy(6f);
        DrawForecastLine();

        ImGuiHelpers.ScaledDummy(6f);
        DrawPriorityOrder();
    }

    /// <summary>
    /// The one slider. It replaced four weights that fed a simulation, none of which could be
    /// pointed at when someone asked why the plan had chosen a particular person.
    /// </summary>
    private void DrawSpread()
    {
        var rules = config.Rules;

        ImGui.TextUnformatted("Share the loot out");
        Widgets.HelpMarker("Left: the top of the order takes everything it can use — one player " +
                           "geared as fast as the raid allows.\n\n" +
                           "Right: every drop goes to whoever is furthest behind, wherever they sit " +
                           "in the list.\n\n" +
                           "In between the two are mixed on one scale: an item already won counts as " +
                           "one place in the order. So a player at the top who has won three pieces " +
                           "is passed at 0.25, one who has won a single piece at 0.50, and one who " +
                           "has won nothing is never passed at all.\n\n" +
                           "The role order above is a gate on top of this, so sharing the loot out " +
                           "shares it within a role rather than handing it past one.");

        var spread = (float)rules.Spread;

        // Half the window rather than all of it: a slider only has to be long enough to aim with,
        // and the two labels under it have to stay attached to its ends to mean anything.
        var left = ImGui.GetCursorPosX();
        var width = System.Math.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.5f);

        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderFloat("##spread", ref spread, 0f, 1f, "%.2f"))
        {
            rules.Spread = spread;
            config.Save();
        }

        // Both ends named, because "0.35" says nothing at all about which way it leans.
        ImGui.TextDisabled("Full priority loot");
        ImGui.SameLine();

        var rightEdge = left + width - ImGui.CalcTextSize("Broad loot distribution").X;
        if (rightEdge > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(rightEdge);

        ImGui.TextDisabled("Broad loot distribution");

        Widgets.Coloured(Widgets.Muted, spread switch
        {
            <= 0.05f => "Everything to the top of the order.",
            <= 0.35f => "The top of the order, unless someone below has won a good deal more.",
            < 0.65f => "Position and what people have already won, evenly weighed.",
            < 0.95f => "Mostly to whoever has won least.",
            _ => "Always to whoever has won least.",
        });
    }

    /// <summary>
    /// The order roles get geared in. This used to be one slider called "damage dealer priority",
    /// which could not say anything at all about tanks against healers — the two most common thing
    /// a static has an opinion about after that.
    /// </summary>
    private void DrawRoleOrder()
    {
        var rules = config.Rules;
        rules.EnsureComplete();

        ImGui.TextUnformatted("Gear roles in this order");
        Widgets.HelpMarker("Damage, then tanks, then healers is the usual answer. Move a role with " +
                           "the arrows.");

        RaidRole? moved = null;
        var direction = 0;

        for (var i = 0; i < rules.RoleOrder.Count; i++)
        {
            var role = rules.RoleOrder[i];
            using var id = ImRaii.PushId((int)role);

            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.SmallButton("^"))
                {
                    moved = role;
                    direction = -1;
                }
            }

            ImGui.SameLine(0f, 2f);

            using (ImRaii.Disabled(i == rules.RoleOrder.Count - 1))
            {
                if (ImGui.SmallButton("v"))
                {
                    moved = role;
                    direction = 1;
                }
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.  {role}");
        }

        if (moved != null)
        {
            rules.Move(moved.Value, direction);
            config.Save();
        }

        var useRoles = rules.UseRoleOrder;
        if (ImGui.Checkbox("Gear by role order", ref useRoles))
        {
            rules.UseRoleOrder = useRoles;
            config.Save();
        }

        Widgets.HelpMarker("On: the order above is a gate. A healer waits while a tank still wants " +
                           "the same piece, whatever else the rules would prefer — which is what a " +
                           "group means when it says it gears damage first. This is the default.\n\n" +
                           "Off: role is ignored entirely, and the player order and the slider below " +
                           "decide on their own.");
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

        var result = planner.Schedule();

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
    /// The player order. With the slider left of centre this is what decides most drops, so it is a
    /// real setting rather than just how the table happens to be sorted. Drag to reorder.
    /// </summary>
    private void DrawPriorityOrder()
    {
        var rules = config.Rules;

        ImGui.TextUnformatted("Player order");
        Widgets.HelpMarker("Who comes first inside a role. How much it counts is the slider above: " +
                           "at the left it decides outright, at the right it only breaks ties. Drag a " +
                           "name to move it.");

        var usePlayers = rules.UsePlayerOrder;
        if (ImGui.Checkbox("Gear in this order", ref usePlayers))
        {
            rules.UsePlayerOrder = usePlayers;
            config.Save();
        }

        Widgets.HelpMarker("Off: everyone inside a role is equal, and drops go by who has won least " +
                           "and has most left. The slider then has nothing to weigh position against.");

        if (roster.Members.Count == 0)
            return;

        using var faded = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f, !usePlayers);

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

            // Under the radio it belongs to, and drawn nowhere else, because it applies nowhere
            // else. The two assigning modes have no use for the off position: they click the loot
            // window and record nothing themselves, so chat is the only thing that knows an award
            // landed. Leaving the box on screen there would offer a switch whose only setting is on.
            if (mode == AssignmentMode.SuggestOnly && config.Mode == AssignmentMode.SuggestOnly)
                DrawChatTicking();
        }
    }

    /// <summary>
    /// Whether the plugin may tick a piece off when chat says somebody received it.
    ///
    /// A real question only while the plugin is not assigning anything: a group that keeps its lists
    /// by hand may well not want them edited from under it. Once the plugin is doing the handing
    /// out, chat is the only witness it has and this stops being a choice.
    /// </summary>
    private void DrawChatTicking()
    {
        using var indent = ImRaii.PushIndent();

        var chat = config.TickOffFromChat;
        if (ImGui.Checkbox("Still tick lists off from chat", ref chat))
        {
            config.TickOffFromChat = chat;
            config.Save();
        }

        Widgets.HelpMarker("When somebody in the party receives a piece the tier knows, tick it off " +
                           "and write it into the history — even though the plugin assigned " +
                           "nothing.\n\n" +
                           "Turn it off to keep the sheet entirely by hand. The lines are still read " +
                           "and still shown in the loot window; what stops is the plugin acting on " +
                           "them.\n\n" +
                           "Only offered here. Under either assigning mode the obtain line is the " +
                           "only proof an award landed, so it is always acted on.");

        if (!config.TickOffFromChat)
        {
            Widgets.Coloured(Widgets.Wanted,
                             "Nothing is ticked off on its own. Every piece has to be recorded by hand.");
        }
    }

    /// <summary>
    /// When and how to be told the raid is starting.
    ///
    /// Outside the read-only gate, and that is the point: the schedule is the group's, but being
    /// reminded of it is not. Somebody with read access is exactly the person a reminder is for.
    /// </summary>
    private void DrawReminders()
    {
        ImGui.TextUnformatted("Reminders");
        Widgets.HelpMarker("Warns you before the static's raid nights. The nights themselves are set " +
                           "in Manage statics; these settings are yours and travel nowhere.");
        ImGui.Separator();

        var schedule = config.Current.Settings.Schedule;

        if (schedule.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted,
                             "This static has no raid nights yet — set them in Manage statics.");
        }

        var notify = config.RemindByNotification;
        if (ImGui.Checkbox("A notification", ref notify))
        {
            config.RemindByNotification = notify;
            config.Save();
        }

        Widgets.HelpMarker("The toast at the corner of the screen. Appears whether or not any " +
                           "LootMastr window is open.");

        ImGui.SameLine(0f, 20f);

        var chat = config.RemindByChat;
        if (ImGui.Checkbox("A line in chat", ref chat))
        {
            config.RemindByChat = chat;
            config.Save();
        }

        Widgets.HelpMarker("Easy to miss in a busy log, and easy to scroll back to. Both are true.");

        ImGui.SameLine(0f, 20f);

        var dtr = config.RemindInDtrBar;
        if (ImGui.Checkbox("A countdown by the clock", ref dtr))
        {
            config.RemindInDtrBar = dtr;
            config.Save();
        }

        Widgets.HelpMarker("A server info bar entry that counts down and then says how long is left " +
                           "of the session. Nothing interrupts you; it is simply there.");

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextDisabled("Warn me:");

        // Fixed lead times rather than a free number: the useful warnings are few and different in
        // kind, and a text field would invite somebody to type 90 and wonder why it never fired.
        foreach (var minutes in new[] { 120, 60, 30, 15, 10, 5 })
        {
            ImGui.SameLine();

            var on = config.ReminderMinutes.Contains(minutes);

            using var colour = ImRaii.PushColor(ImGuiCol.Text, on ? Widgets.Done : Widgets.Muted);

            if (!ImGui.SmallButton(minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h"))
                continue;

            if (on)
                config.ReminderMinutes.Remove(minutes);
            else
                config.ReminderMinutes.Add(minutes);

            config.Save();
        }

        ImGui.SameLine(0f, 16f);
        ImGui.TextDisabled("and at the start.");
        Widgets.HelpMarker("The start itself is always announced. A warning \"0 minutes before\" " +
                           "would read as a mistake, and it is the one nobody wants to miss.");
    }

    /// <summary>
    /// Second characters, which a funnel group has in the party and does not want to gear.
    ///
    /// Three settings and they narrow in order: whether alts exist at all, what they may be given,
    /// and the one thing they are genuinely a good home for. The two that follow are drawn only when
    /// the first is on, because off means the whole idea is gone.
    /// </summary>
    private void DrawAltCharacters()
    {
        var enabled = config.AltCharacters;
        if (ImGui.Checkbox("The roster has alt characters in it", ref enabled))
        {
            config.AltCharacters = enabled;
            config.Save();
        }

        Widgets.HelpMarker("On: players marked as alts appear in the plan and the loot window, and " +
                           "take nothing except the weapon stone and its material.\n\n" +
                           "Off: they are left out of the plan, the forecast, the ranking and every " +
                           "selector, as if they were not in the roster. They stay in the roster " +
                           "list so they can be brought back.");

        if (!enabled)
        {
            Widgets.Coloured(Widgets.Muted, "Mark a player as an alt in Manage statics.");
            return;
        }

        var spare = config.Rules.AltsMayTakeSpareGear;
        if (ImGui.Checkbox("Alts may take gear no main still needs", ref spare))
        {
            config.Rules.AltsMayTakeSpareGear = spare;
            config.Save();
        }

        Widgets.HelpMarker("A last resort, never a competitor: an alt is only ever offered a coffer " +
                           "after every main character has passed on it.\n\n" +
                           "Off, a coffer nobody needs is greed. On, it goes on the second character " +
                           "instead of nowhere.");

        var weapons = config.AltsPreferredForWeaponTokens;
        if (ImGui.Checkbox("Offer the weapon stone to alts first", ref weapons))
        {
            config.AltsPreferredForWeaponTokens = weapons;
            config.Save();
        }

        Widgets.HelpMarker("The one thing an alt is genuinely a good home for. A tomestone weapon on " +
                           "a second character costs the raid nothing and makes the next clear go " +
                           "faster, which is the entire reason the character exists.");
    }

    /// <summary>
    /// The mount, which is the one thing in the chest that is worth nothing to the raid.
    ///
    /// A pair of policies rather than a switch with an off position: handing it out in reverse of
    /// the gear order and rolling for it are both things groups actually do, and neither is the
    /// absence of the other.
    /// </summary>
    private void DrawMountChoice()
    {
        foreach (var (mode, label, help) in MountModes)
        {
            if (ImGui.RadioButton(label, config.Mount == mode))
            {
                config.Mount = mode;
                config.Save();
            }

            Widgets.HelpMarker(help);
        }
    }

    private static readonly (MountHandling Mode, string Label, string Help)[] MountModes =
    [
        (MountHandling.Assign, "Give the mount to somebody",
            "The Loot tab offers the roster in reverse of the gear order — healers first where the " +
            "gear rules put them last — then whoever has won fewest items, and never anyone who " +
            "already has one.\n\n" +
            "You still pick and press."),
        (MountHandling.GreedOnly, "Put the mount up for greed",
            "One button that sets the mount to greed only and lets the dice decide.\n\n" +
            "It asks first, because the game does not: greed only settles an item for good with no " +
            "confirmation of its own."),
    ];

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
