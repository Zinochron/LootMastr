using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    private SimulationResult? forecast;
    private int forecastSignature;

    /// <summary>Who is selected for each special drop, by member key. Empty means nobody yet.</summary>
    private readonly Dictionary<SpecialDrop, string> chosen = new();

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

    /// <summary>
    /// Hidden from readers entirely.
    ///
    /// Everything here either writes to the static or acts on the loot window as party leader, and a
    /// reader is neither. What would be left is a table of buttons nobody can press, above a chest
    /// somebody else is handing out — the one tab where read-only leaves nothing behind.
    /// </summary>
    public bool UsefulToReaders => false;

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

            DrawSpecialDrops();

            ImGuiHelpers.ScaledDummy(6f);
            DrawActions();
        }

        ImGuiHelpers.ScaledDummy(10f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(6f);
        DrawThisWeek();

        ImGuiHelpers.ScaledDummy(8f);
        DrawBooks();
    }

    /// <summary>
    /// Every coffer this reset can put up, and who it is for.
    ///
    /// The same answer the Plan tab gives, in the tab somebody actually has open with a chest in
    /// front of them. It is not a duplicate of the loot list above: that one is what dropped, this
    /// is what is still coming — which is the question when the chest holds two things and somebody
    /// asks whether to pass.
    ///
    /// Ranked with the earlier fights already handed out, so nobody is named twice for a piece they
    /// can only wear once.
    /// </summary>
    private void DrawThisWeek()
    {
        if (!ImGui.CollapsingHeader("This week###thisWeek"))
            return;

        Widgets.HelpMarker("Every coffer the coming reset can drop, whether or not it has yet. The " +
                           "Plan tab shows the same thing with the runners-up and the reasoning.");

        var week = Forecast().Awards.Where(a => a is { Week: 1, Bought: false }).ToList();

        if (week.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing expected — everyone is done, or no tier is set up.");
            return;
        }

        using var table = ImRaii.Table("##thisWeek", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("##fight", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##drops", ImGuiTableColumnFlags.WidthStretch);

        // Fights are told apart by a band of colour rather than by a line, because a table border
        // sits between every row — which would separate a fight's own coffers exactly as strongly as
        // it separates the fights, and that is the opposite of what is wanted here.
        var banded = false;

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            var mine = week.Where(a => a.Encounter == encounter.Index).ToList();
            var tint = banded ? ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.035f)) : 0u;

            banded = !banded;

            if (mine.Count == 0)
            {
                Row(encounter.Name, tint);
                Widgets.Coloured(Widgets.Muted, "nothing");
                continue;
            }

            for (var i = 0; i < mine.Count; i++)
            {
                // The fight is named once, against its first coffer. Repeating it down the group
                // would read as four separate fights that happen to share a name.
                Row(i == 0 ? encounter.Name : string.Empty, tint);

                ImGui.TextUnformatted(mine[i].What);
                ImGui.SameLine();
                ImGui.TextDisabled("→");
                ImGui.SameLine();
                Widgets.Coloured(Widgets.Done, mine[i].PlayerName);
            }
        }

        return;

        static void Row(string fight, uint tint)
        {
            ImGui.TableNextRow();

            if (tint != 0)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, tint);

            ImGui.TableNextColumn();

            if (fight.Length > 0)
                ImGui.TextDisabled(fight);

            ImGui.TableNextColumn();
        }
    }

    /// <summary>
    /// Kills per fight and books per player, in one grid. These are the numbers the whole forecast
    /// rests on and the ones the game gives no way to read, so they have to be both visible and
    /// editable — a book counted wrong is invisible everywhere else.
    /// </summary>
    /// <summary>
    /// Kills, books and what they buy.
    ///
    /// Folded away, and it earns its place folded. Two of the three things in it live nowhere else —
    /// the group's kill counts, and the button that hands everyone a book for a clear the plugin did
    /// not see — while the per-player counts are also on the roster row. What is worth having open
    /// during a pull is above this; this is for the ten minutes after one.
    /// </summary>
    private void DrawBooks()
    {
        if (!ImGui.CollapsingHeader("Books and kills###books"))
            return;

        // Book and kill counts are the group's numbers. A reader may read them -- knowing what they
        // are holding is half the point of being shown any of this -- and may not change them.
        using var gate = ImRaii.Disabled(!config.CanWrite);

        Widgets.HelpMarker("Every player who clears a fight gets one of its books that week. Kills are " +
                           "the group's count; the rows below are what each player is holding right now, " +
                           "after anything they have already spent.");

        var encounters = tiers.Tier.Encounters.OrderBy(e => e.Index).ToList();
        if (encounters.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No fights defined — open the tier from the header.");
            return;
        }

        using var table = ImRaii.Table("##books", encounters.Count + 2,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);

        foreach (var encounter in encounters)
            ImGui.TableSetupColumn(encounter.Name, ImGuiTableColumnFlags.WidthFixed, 78f * ImGuiHelpers.GlobalScale);

        ImGui.TableSetupColumn("Should buy now", ImGuiTableColumnFlags.WidthStretch);
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

        var buy = planner.ShouldBuyNow(Forecast(), member);

        if (buy.Count == 0)
            ImGui.TextDisabled("—");
        else
            Widgets.Coloured(Widgets.Done, string.Join(", ", buy));
    }

    /// <summary>
    /// The plan, kept until something it reads changes. The book column needs it every frame and it
    /// is far too much work to redo at that rate.
    /// </summary>
    private SimulationResult Forecast()
    {
        var signature = roster.Signature();

        if (forecast == null || signature != forecastSignature)
        {
            forecastSignature = signature;
            forecast = planner.ComingWeek();
        }

        return forecast;
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

        // A reader is told the clear was noticed and is not offered the buttons: handing out books is
        // the leader's, and a prompt whose only answer is "no" is a prompt that should not appear.
        if (!config.CanWrite)
        {
            Widgets.Coloured(Widgets.Muted,
                             $"{clears.Pending.Name} cleared. Whoever keeps this static adds the books.");

            ImGuiHelpers.ScaledDummy(6f);
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
        var verdict = assigner.Verdict;

        using var table = ImRaii.Table("##decisions", 5,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        // One button per box rather than a single "next": which coffer is being handed over is the
        // leader's call, and a list that decides for you goes wrong the moment it thinks an item is
        // still open when it is not.
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 62f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Goes to", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Then", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var decision in assigner.Decisions)
        {
            // Owned by the section below. Listing it in both places would offer the same stone
            // twice, from two different orders.
            if (tiers.Tier.SpecialFor(decision.Item.ItemId) != null)
                continue;

            using var id = ImRaii.PushId(decision.Item.Index);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (decision.AlreadyAssigned)
            {
                ImGui.AlignTextToFramePadding();
                Widgets.Coloured(Widgets.Done, "done");
                Widgets.Tooltip("Someone obtained this — it is out of the chest's running.");
            }
            else
            {
                using (ImRaii.Disabled(!verdict.Ok || assigner.IsAssigning || decision.Winner == null))
                {
                    if (ImGui.SmallButton("Assign"))
                    {
                        if (!assigner.PerformAssignment(decision, out var reason))
                            Services.Chat.PrintError($"LootMastr: {reason}");
                    }
                }

                if (!verdict.Ok)
                    Widgets.Tooltip(verdict.Reason);
                else if (decision.Offered)
                {
                    // Offered, not done. The plugin drove the windows; whether the item moved is
                    // something only the obtain line in chat can say, and the game does refuse a
                    // coffer the recipient already owns.
                    Widgets.Tooltip("Already put in front of the game once — waiting for someone to " +
                                    "obtain it. Press again if the game refused it.");
                }
            }

            ImGui.TableNextColumn();
            Widgets.Icon(decision.Item.IconId, 18f);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(decision.Item.Name);

            if (decision.Offered && !decision.AlreadyAssigned)
            {
                ImGui.SameLine();
                Widgets.Coloured(Widgets.Wanted, "(offered)");
            }

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
            using (ImRaii.Disabled(decision.Winner == null || !config.CanWrite))
            {
                if (ImGui.SmallButton("Record"))
                    assigner.ConfirmByHand(decision);
            }

            Widgets.Tooltip("Tick this off as received and take it out of the chest's running. " +
                            "Normally unnecessary: the obtain line in chat does both on its own. " +
                            "Use it if chat tracking is off, or was not listening.");
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

    /// <summary>
    /// The drops that no need list describes: the weapon stone, the material that augments what it
    /// buys, and the mount.
    ///
    /// Drawn only when one is actually in the chest. A section that is always there would be three
    /// empty rows in front of the leader every pull, for something that happens once a week.
    ///
    /// The plugin supplies an order and never an answer. Who takes the stone is a conversation —
    /// about who is closest to done with the vendor, and about who wants it — and a plugin that
    /// picked for you would be wrong in a way nobody could see.
    /// </summary>
    private void DrawSpecialDrops()
    {
        var specials = assigner.Decisions
                               .Select(d => (Decision: d, Kind: tiers.Tier.SpecialFor(d.Item.ItemId)))
                               .Where(x => x.Kind != null)
                               .ToList();

        if (specials.Count == 0)
            return;

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.TextUnformatted("Decided by hand");
        Widgets.HelpMarker("Drops that fill no gear slot, so no ranking can answer them. The order " +
                           "in the list is a suggestion — the pick is yours.");
        ImGui.Separator();

        foreach (var (decision, kind) in specials)
            DrawSpecialAssignment(decision, kind!.Value);
    }

    private void DrawSpecialAssignment(LootDecision decision, SpecialDrop kind)
    {
        using var id = ImRaii.PushId($"special{(int)kind}");

        var order = OrderFor(kind);
        var verdict = assigner.Verdict;

        Widgets.Icon(decision.Item.IconId, 20f);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(decision.Item.Name);

        ImGui.SameLine();
        ImGui.TextDisabled($"— {NoteFor(kind)}");

        if (decision.AlreadyAssigned)
        {
            ImGui.SameLine();
            Widgets.Coloured(Widgets.Done, "done");
            return;
        }

        if (kind == SpecialDrop.Mount && config.Mount == MountHandling.GreedOnly)
        {
            DrawGreedOnly(decision, verdict);
            return;
        }

        if (order.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted,
                             kind == SpecialDrop.Mount
                                 ? "Everybody in the roster already has one."
                                 : "Nobody left in the roster for this one.");
            return;
        }

        // Preselected to the top of the order, so the common case is one click — but it is a
        // selection like any other and reads as one, rather than as the plugin having decided.
        if (!chosen.TryGetValue(kind, out var key) || order.All(e => e.Member.Key != key))
        {
            key = order[0].Member.Key;
            chosen[kind] = key;
        }

        var selected = order.First(e => e.Member.Key == key);

        if (ImGui.SmallButton($"{selected.Member.Name}##pick"))
            ImGui.OpenPopup("##specialPick");

        Widgets.Tooltip(selected.Note);

        ImGui.SameLine();

        using (ImRaii.Disabled(!verdict.Ok || assigner.IsAssigning))
        {
            if (ImGui.SmallButton("Assign"))
            {
                if (!assigner.PerformAssignment(decision.Item, selected.Member.Name, out var reason))
                    Services.Chat.PrintError($"LootMastr: {reason}");
            }
        }

        if (!verdict.Ok)
            Widgets.Tooltip(verdict.Reason);

        ImGui.SameLine();

        using (ImRaii.Disabled(!config.CanWrite))
        {
            if (ImGui.SmallButton("Record"))
                assigner.RecordSpecial(decision.Item, selected.Member, kind, LootSource.ByHand);
        }

        Widgets.Tooltip("Tick this off as received. Nothing in chat can do it for these — they are " +
                        "not tier loot as far as the rest of the plugin is concerned.");

        using var popup = ImRaii.Popup("##specialPick");
        if (!popup.Success)
            return;

        foreach (var entry in order)
        {
            using var colour = ImRaii.PushColor(ImGuiCol.Text, Widgets.Wanted, entry.Highlight);

            if (ImGui.Selectable($"{entry.Member.Name}##{entry.Member.Key}"))
                chosen[kind] = entry.Member.Key;

            ImGui.SameLine();
            ImGui.TextDisabled(entry.Note);
        }
    }

    /// <summary>
    /// The one press in this plugin that nothing can take back.
    ///
    /// Greed only settles an item for good and the game shows no confirmation of its own, so this
    /// one does. Not a Ctrl+click either: that is the right weight for removing a player from a
    /// list, and the wrong weight for something that happens in front of seven other people and
    /// cannot be undone.
    /// </summary>
    private void DrawGreedOnly(LootDecision decision, GuardVerdict verdict)
    {
        using (ImRaii.Disabled(!verdict.Ok || assigner.IsAssigning))
        {
            if (ImGui.SmallButton("Set to greed only"))
                ImGui.OpenPopup("##greedConfirm");
        }

        if (!verdict.Ok)
            Widgets.Tooltip(verdict.Reason);

        using var popup = ImRaii.Popup("##greedConfirm");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted($"Put {decision.Item.Name} up for greed?");
        Widgets.Coloured(Widgets.Wanted, "This cannot be undone, and the game will not ask again.");

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button("Greed only"))
        {
            if (!assigner.SetGreedOnly(decision.Item, out var reason))
                Services.Chat.PrintError($"LootMastr: {reason}");

            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    /// <summary>One line of a special drop's list: who, why they are there, and whether to shout.</summary>
    private readonly record struct SpecialCandidate(RosterMember Member, string Note, bool Highlight);

    private IReadOnlyList<SpecialCandidate> OrderFor(SpecialDrop kind)
    {
        var standings = planner.ByTomeProgress();

        if (kind == SpecialDrop.Mount)
        {
            // Backwards, on purpose. The mount is the one thing in the chest that is worth nothing
            // to the raid, so it goes to whoever the gear rules have been putting last.
            var order = config.Rules.RoleOrder.ToList();

            var active = roster.Active.ToList();

            return active
                   .Where(m => !m.MountObtained)
                   .OrderBy(m => m.IsAlt)
                   .ThenByDescending(m => order.IndexOf(roster.RoleOf(m)))
                   .ThenBy(m => m.ItemsReceived)
                   .ThenBy(active.IndexOf)
                   .Select(m => new SpecialCandidate(
                               m, $"{roster.RoleOf(m)}, {m.ItemsReceived} item(s) so far", false))
                   .ToList();
        }

        return standings
               .Select(s => new SpecialCandidate(s.Member, NoteFor(s, kind), Highlighted(s.Member, kind)))
               .OrderByDescending(c => c.Highlight)
               .ThenByDescending(c => config.AltsPreferredForWeaponTokens && c.Member.IsAlt)
               .ToList();
    }

    /// <summary>
    /// The fight-three material is only worth anything to somebody holding a stone from fight two.
    /// Everyone else is listed, because plans change, but they are not what the list is pointing at.
    /// </summary>
    private static bool Highlighted(RosterMember member, SpecialDrop kind) =>
        kind == SpecialDrop.WeaponAugment && member.WeaponTokenObtained && !member.WeaponAugmentObtained;

    private static string NoteFor(TomeStanding standing, SpecialDrop kind)
    {
        var progress = standing.Owed == 0
                           ? "vendor done"
                           : $"{standing.Owed:N0} tomes left, W{standing.Week}";

        if (kind != SpecialDrop.WeaponAugment)
            return standing.Member.WeaponTokenObtained ? $"{progress}, has a stone" : progress;

        if (standing.Member.WeaponAugmentObtained)
            return $"{progress}, already augmented";

        return standing.Member.WeaponTokenObtained ? $"{progress}, has a stone" : $"{progress}, no stone";
    }

    private static string NoteFor(SpecialDrop kind) => kind switch
    {
        SpecialDrop.WeaponToken => "buys the tomestone weapon, with 500 tomestones",
        SpecialDrop.WeaponAugment => "augments the tomestone weapon",
        _ => "one each, and only once",
    };

    private void DrawActions()
    {
        ImGui.TextDisabled(config.Mode == AssignmentMode.Automatic
                               ? "Assign opens the window, picks the player, and answers the game's " +
                                 "confirmation once it names the right player and item."
                               : "Assign opens the window and picks the player, then leaves the game's " +
                                 "own \"Allow X to claim Y?\" for you.");

        if (assigner.IsAssigning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                assigner.StopAssigning("Stopped.");
        }

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
