using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr.UI.Tabs;

public sealed class SettingsTab : ITab
{
    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly LootPlanner planner;

    // Three projections behind the comparison line, cleared by the same roster fingerprint the Plan
    // tab uses. Cheap once and absurd sixty times a second.
    private Dictionary<NeedBasis, int>? basisWeeks;
    private int basisSignature;

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

        // Under the working mode, because it is the setting that mode exists for: simple mode
        // measures no damage, so there is nothing here to choose between and it is not drawn.
        if (config.ExpertMode)
        {
            ImGuiHelpers.ScaledDummy(6f);
            DrawRanking();
        }

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
    /// What "needs it more" should mean. Only in expert mode, because the damage answer needs
    /// everyone's gear read and there is nothing to choose between otherwise.
    ///
    /// The comparison line is the point of this. Both plans are cheap to run, so rather than telling
    /// somebody that maximising damage might cost the group a week, it runs both and says whether it
    /// does — for their roster, this week.
    /// </summary>
    private void DrawRanking()
    {
        ImGui.TextUnformatted("Ranking");
        Widgets.HelpMarker("What the sharing-out end of the slider measures, and nothing else.\n\n" +
                           "The role order and the player order below still decide: role is a gate " +
                           "above this, and position is the other half of the slider. Ranking by " +
                           "damage does not switch them off.");
        ImGui.Separator();

        if (!planner.CanRankByDamage)
        {
            // "Nobody's gear has been read" was wrong in the case that actually happens: a scan
            // writes the gear whether or not the stats came with it, and the stats are the half this
            // needs. Telling somebody to do a thing they have already done is worse than saying
            // nothing.
            Widgets.Coloured(Widgets.Muted,
                             roster.Members.Any(m => m.HasBeenScanned)
                                 ? "Gear has been read, but nobody's stats came with it — those only " +
                                   "exist while the examine window is open. Read gear again on the " +
                                   "Roster tab."
                                 : "Nobody's gear has been read, so there is no damage to rank by. " +
                                   "Read gear on the Roster tab.");

            return;
        }

        // Anyone unrated scores a zero gain, which under a damage ranking is indistinguishable from
        // a player nothing is worth anything to. Silent, and it decides who gets a coffer.
        var unrated = roster.Active.Where(m => !m.HasMeasuredStats).Select(m => m.Name).ToList();

        if (unrated.Count > 0)
        {
            Widgets.Coloured(Widgets.Wanted,
                             $"No stats for {string.Join(", ", unrated)} — they rank as gaining " +
                             "nothing. Read their gear again on the Roster tab.");
        }

        var rules = config.Rules;

        foreach (var (basis, label, help) in Bases)
        {
            if (ImGui.RadioButton(label, rules.Basis == basis) && rules.Basis != basis)
            {
                rules.Basis = basis;
                config.Save();
                basisWeeks = null;
            }

            Widgets.HelpMarker(help);
            ImGui.SameLine(0f, 16f);
        }

        ImGui.NewLine();
        DrawBasisComparison();
    }

    private static readonly (NeedBasis Basis, string Label, string Help)[] Bases =
    [
        (NeedBasis.MissingGear, "By missing gear",
            "Whoever has won least. A rule about people, and the one that needs no gear read at all."),
        (NeedBasis.DpsGain, "By damage gain",
            "Whoever the piece is worth most to, and nothing else. Somebody who has already had four " +
            "pieces keeps getting them if that is where the damage is.\n\n" +
            "Measured in flat damage per second, not as a share of what they already do — a healer " +
            "gaining a lot of their own output is fewer points of raid damage than a melee gaining a " +
            "little of theirs, and the group only feels the points.\n\n" +
            "The catch: comparing two different jobs leans on their rotation profiles, which are " +
            "modelled rather than measured. Two players of the same job are compared exactly."),
        (NeedBasis.Both, "By both",
            "On one scale: a hundred points of damage and an item already won weigh the same. Somebody " +
            "who has had four pieces needs to gain four hundred more than the next player to stay ahead."),
    ];

    /// <summary>
    /// What choosing damage over gear costs, or saves, run rather than asserted.
    ///
    /// The honest worry about maximising damage is that it strands somebody: the group's last player
    /// finishes later because every coffer went where it helped most rather than where it was needed.
    /// That is a question with an answer, and both runs are cheap.
    /// </summary>
    private void DrawBasisComparison()
    {
        var signature = roster.Signature();

        if (basisWeeks == null || signature != basisSignature)
        {
            basisSignature = signature;
            basisWeeks = CompareBases();
        }

        var weeks = basisWeeks;
        var mine = weeks[config.Rules.Basis];
        var best = weeks.Values.Min();

        if (mine <= best)
        {
            Widgets.Coloured(Widgets.Done, $"Everyone is geared in week {mine} — no other ranking is faster.");
        }
        else
        {
            var faster = weeks.First(w => w.Value == best);
            var name = Bases.First(b => b.Basis == faster.Key).Label.ToLowerInvariant();

            Widgets.Coloured(Widgets.Wanted,
                             $"This costs the group {mine - best} week(s): {mine} against {best} {name}.");
        }

        Widgets.Tooltip(string.Join("\n", Bases.Select(b => $"{b.Label}: everyone geared in week {weeks[b.Basis]}")));
    }

    /// <summary>
    /// Every ranking's finish week, run once and cached with the rest of the plan.
    ///
    /// Three full projections, each of which measures a damage gain per open need. Cheap once and
    /// absurd sixty times a second — this used to run on every frame the tab was open.
    ///
    /// The chosen basis is swapped out and back under a <c>finally</c>: the planner reads it from the
    /// live rules, and leaving somebody's setting on whatever the last comparison used would be a
    /// silent config change.
    /// </summary>
    private Dictionary<NeedBasis, int> CompareBases()
    {
        var rules = config.Rules;
        var chosen = rules.Basis;
        var weeks = new Dictionary<NeedBasis, int>();

        try
        {
            foreach (var basis in new[] { NeedBasis.MissingGear, NeedBasis.DpsGain, NeedBasis.Both })
            {
                rules.Basis = basis;
                weeks[basis] = planner.Schedule().LastFinishWeek;
            }
        }
        finally
        {
            rules.Basis = chosen;
        }

        return weeks;
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

        if (DrawRoleList(rules.RoleOrder, "gearRoles"))
            config.Save();

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
    /// real setting rather than just how the table happens to be sorted.
    ///
    /// The list appears only once the switch is on. Faded out it read as decoration — a thing to
    /// look at rather than a thing that was about to do nothing — and it is the longest block on the
    /// tab, so a group not using it was scrolling past its own roster to reach the rest.
    ///
    /// Arrows rather than dragging. ImGui's drag and drop needs the pointer to be over the target
    /// row on the frame the button comes up, and on a list this tall a fast drag lands between two
    /// rows and drops nothing, with no way to tell that from a refused move.
    /// </summary>
    private void DrawPriorityOrder()
    {
        var rules = config.Rules;

        ImGui.TextUnformatted("Player order");
        Widgets.HelpMarker("Who comes first inside a role. How much it counts is the slider above: " +
                           "at the left it decides outright, at the right it only breaks ties.");

        var usePlayers = rules.UsePlayerOrder;
        if (ImGui.Checkbox("Gear in this order", ref usePlayers))
        {
            rules.UsePlayerOrder = usePlayers;
            config.Save();
        }

        Widgets.HelpMarker("Off: everyone inside a role is equal, and drops go by who has won least " +
                           "and has most left. The slider then has nothing to weigh position against.");

        if (!usePlayers)
            return;

        var members = roster.Members;

        if (members.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No players yet.");
            return;
        }

        using var indent = ImRaii.PushIndent();

        var from = -1;
        var direction = 0;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            using var id = ImRaii.PushId(member.Key);

            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.SmallButton("^"))
                {
                    from = i;
                    direction = -1;
                }
            }

            ImGui.SameLine(0f, 2f);

            using (ImRaii.Disabled(i == members.Count - 1))
            {
                if (ImGui.SmallButton("v"))
                {
                    from = i;
                    direction = 1;
                }
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.  {member.Name}   ({roster.RoleOf(member)})");
        }

        // After the loop, never inside it: moving a member while the list is being walked draws one
        // row twice and skips another, which looks exactly like the move having gone wrong.
        if (from < 0)
            return;

        var moved = members[from];
        members.RemoveAt(from);
        members.Insert(from + direction, moved);
        config.Save();
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

            // The order only means anything while the mount is being handed out. Under greed only
            // the dice decide and there is nothing for a priority to say.
            if (mode == MountHandling.Assign && config.Mount == MountHandling.Assign)
                DrawMountOrder();
        }
    }

    /// <summary>
    /// Which of the two rules picks the mount's recipient, and the list when it is the second.
    ///
    /// This used to be one sentence of help text describing the gear order read backwards, which is
    /// the kind of rule that can only be explained and never checked. Two named choices, and where
    /// the group wants its own answer, a list that says it outright.
    /// </summary>
    private void DrawMountOrder()
    {
        using var indent = ImRaii.PushIndent();

        foreach (var (order, label, help) in MountOrders)
        {
            if (ImGui.RadioButton(label, config.MountOrder == order))
            {
                config.MountOrder = order;
                config.Save();
            }

            Widgets.HelpMarker(help);
        }

        if (config.MountOrder != MountPriority.ByRole)
            return;

        ImGuiHelpers.ScaledDummy(2f);

        using var deeper = ImRaii.PushIndent();

        if (DrawRoleList(config.MountRoleOrder, "mountRoles"))
            config.Save();
    }

    /// <summary>
    /// A list of roles with a pair of arrows each. Returns true when one moved.
    ///
    /// Shared by the gear order and the mount's own, which is the whole reason it is a method: two
    /// lists drawn by two copies of this loop is how they drift apart.
    /// </summary>
    private static bool DrawRoleList(List<RaidRole> order, string id)
    {
        var complete = Roles.Complete(order);

        if (!complete.SequenceEqual(order))
        {
            order.Clear();
            order.AddRange(complete);
        }

        using var scope = ImRaii.PushId(id);

        RaidRole? moved = null;
        var direction = 0;

        for (var i = 0; i < order.Count; i++)
        {
            var role = order[i];
            using var rowId = ImRaii.PushId((int)role);

            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.SmallButton("^"))
                {
                    moved = role;
                    direction = -1;
                }
            }

            ImGui.SameLine(0f, 2f);

            using (ImRaii.Disabled(i == order.Count - 1))
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

        if (moved == null)
            return false;

        Roles.Move(order, moved.Value, direction);
        return true;
    }

    private static readonly (MountHandling Mode, string Label, string Help)[] MountModes =
    [
        (MountHandling.Assign, "Assign the mount via Lootmaster",
            "The Loot tab offers the roster in the order below, skipping anybody who already has " +
            "one.\n\n" +
            "You still pick and press."),
        (MountHandling.GreedOnly, "Put the mount up for greed",
            "One button that sets the mount to greed only and lets the dice decide.\n\n" +
            "It asks first, because the game does not: greed only settles an item for good with no " +
            "confirmation of its own."),
    ];

    private static readonly (MountPriority Order, string Label, string Help)[] MountOrders =
    [
        (MountPriority.FinishesLast, "To whoever finishes last",
            "Straight out of the forecast: the player the raid still owes gear to for the longest.\n\n" +
            "The mount is the one thing in the chest worth nothing to the raid, so it goes to " +
            "whoever will be turning up for their own sake longest. Ties go to whoever has won " +
            "fewest items."),
        (MountPriority.ByRole, "By a priority of its own",
            "A role order set below, unrelated to the gear order. Healers first by default, which " +
            "is the usual answer where damage is geared first."),
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
