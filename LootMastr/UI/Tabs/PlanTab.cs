using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr.UI.Tabs;

/// <summary>
/// What the rest of the tier looks like, and who each drop goes to. Recomputed on demand rather
/// than every frame: the forecast is cheap but not free.
///
/// There used to be a second table underneath, "If it dropped right now", ranking every kind of
/// drop on its own with nothing handed out. It existed because it was the only view using the
/// ranking directly while everything else went through a different rule. One rule later, it was
/// the same list as the one above with the week's earlier drops ignored — a second answer to a
/// question already answered, which is how a plan loses the reader's trust.
/// </summary>
public sealed class PlanTab : ITab
{
    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly LootPlanner planner;
    private readonly TierCatalog tiers;

    private SimulationResult? coming;
    private SimulationResult? schedule;
    private Dictionary<NeedBasis, int>? basisWeeks;
    private int cachedSignature;

    public PlanTab(Configuration config, RosterStore roster, LootPlanner planner, TierCatalog tiers)
    {
        this.config = config;
        this.roster = roster;
        this.planner = planner;
        this.tiers = tiers;
    }

    public string Title => "Plan";
    public string Id => "plan";

    /// <summary>Called when the roster or the tier changed under us.</summary>
    public void Invalidate()
    {
        coming = null;
        schedule = null;
        basisWeeks = null;
    }

    public void Draw()
    {
        if (roster.Members.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Add players to the roster first.");
            return;
        }

        using (ImRaii.Disabled(!config.CanWrite))
        {
            if (ImGui.Button("Recalculate"))
                Invalidate();
        }

        Widgets.HelpMarker(config.CanWrite
                               ? "Recalculates on its own whenever the roster, a tick or a book count " +
                                 "changes; this is only here for after a tier edit. Weeks are counted " +
                                 "from now, so week 1 is the next reset."
                               : "Only needed after editing the tier, which needs write access. " +
                                 "Everything below already follows the roster and the last pull on " +
                                 "its own.");

        ImGui.SameLine();
        ImGui.TextDisabled($"looking {config.LookaheadWeeks} weeks ahead");

        // Ticking a box in another tab has to reach the numbers here, and a fingerprint is far
        // cheaper than rerunning eight simulations a frame to find out whether it did.
        var signature = roster.Signature();
        if (signature != cachedSignature)
        {
            cachedSignature = signature;
            Invalidate();
        }

        if (config.ExpertMode)
        {
            ImGuiHelpers.ScaledDummy(4f);
            DrawRanking();
        }

        coming ??= planner.ComingWeek();
        schedule ??= planner.Schedule();

        ImGuiHelpers.ScaledDummy(6f);
        DrawForecast(schedule);

        ImGuiHelpers.ScaledDummy(10f);
        DrawNextDrops(coming);

        ImGuiHelpers.ScaledDummy(10f);
        DrawSchedule(schedule);
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
        Widgets.HelpMarker("What the sharing-out end of the slider measures. The role order and the " +
                           "player order are unaffected — this is only the other half.");
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

        // The comparison below is worth reading whoever you are — it is the answer to "would sharing
        // differently get us done sooner". Only choosing between them needs write access.
        using (ImRaii.Disabled(!config.CanWrite))
        {
            foreach (var (basis, label, help) in Bases)
            {
                if (ImGui.RadioButton(label, rules.Basis == basis) && rules.Basis != basis)
                {
                    rules.Basis = basis;
                    config.Save();
                    Invalidate();
                }

                Widgets.HelpMarker(help);
                ImGui.SameLine(0f, 16f);
            }
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
        var weeks = basisWeeks ??= CompareBases();
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

    private void DrawForecast(SimulationResult result)
    {
        ImGui.TextUnformatted("Who finishes when");
        ImGui.Separator();

        DrawHeadline(result);

        using var table = ImRaii.Table("##forecast", 6,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Still needs", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Raid done", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Tomes left", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Set done", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var plans = planner.BuildPlans().ToDictionary(p => p.Key);

        // The active list, not the roster: a second character the group has switched off is not a
        // row with nothing in it, it is not in the plan at all.
        foreach (var member in roster.Active)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(member.Name);

            ImGui.TableNextColumn();
            var role = roster.RoleOf(member);
            if (role == RaidRole.Dps)
                Widgets.Coloured(Widgets.Wanted, role.ToString());
            else
                ImGui.TextUnformatted(role.ToString());

            ImGui.TableNextColumn();
            var open = plans.TryGetValue(member.Key, out var plan) ? plan.Open.Count : 0;
            ImGui.TextUnformatted(open == 0 ? "—" : open.ToString());

            ImGui.TableNextColumn();
            var week = result.FinishWeeks.GetValueOrDefault(member.Key, result.Horizon + 1);

            if (open == 0)
                Widgets.Coloured(Widgets.Done, "done");
            else if (result.BeyondHorizon(week))
                Widgets.Coloured(Widgets.Bad, $"> W{result.Horizon}");
            else
                ImGui.TextUnformatted($"W{week}");

            // What the vendor still wants. A player with no tomestone gear planned reads "—" rather
            // than 0: nothing to buy and "bought it all already" are different states, and only one
            // of them means the set is finished.
            ImGui.TableNextColumn();
            var owed = plan == null ? 0 : TomeLedger.Outstanding(plan);
            var planned = plan != null && (owed > 0 || HasTomeGear(member));

            if (!planned)
                Widgets.Coloured(Widgets.Muted, "—");
            else if (owed == 0)
                Widgets.Coloured(Widgets.Done, "done");
            else
                ImGui.TextUnformatted($"{owed:N0}");

            Widgets.Tooltip(owed == 0
                                ? "Nothing left to buy from the tomestone vendor."
                                : $"{owed:N0} tomestones still to spend, at " +
                                  $"{tiers.Tier.TomestonesPerWeek} a week.");

            ImGui.TableNextColumn();
            var whole = result.WholeSetWeek(member.Key);

            if (open == 0 && owed == 0)
                Widgets.Coloured(Widgets.Done, "done");
            else if (result.BeyondHorizon(whole))
                Widgets.Coloured(Widgets.Bad, $"> W{result.Horizon}");
            else
                Widgets.Coloured(whole > week ? Widgets.Augment : Widgets.Muted, $"W{whole}");

            Widgets.Tooltip("When the whole set is finished — the later of the raid half and the " +
                            "tomestone half.");
        }
    }

    /// <summary>Whether this player's target set has any tomestone gear in it at all.</summary>
    private static bool HasTomeGear(RosterMember member)
    {
        foreach (var slot in Slots.All)
        {
            if (member.NeedFor(slot).Source is GearSource.Tome or GearSource.TomeAugmented)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The two clocks, in one sentence, with the raid one in front.
    ///
    /// That order is deliberate. The raid week is the number a group can do something about — clear
    /// more, share differently — and it is the one every loot decision moves. The tomestone week is
    /// a fact about the calendar, and putting it second says so without hiding it.
    /// </summary>
    private void DrawHeadline(SimulationResult result)
    {
        if (result.BeyondHorizon(result.LastFinishWeek))
        {
            Widgets.Coloured(Widgets.Wanted,
                             $"Not everyone is done within {result.Horizon} weeks. Raise the horizon in " +
                             "Settings to see how much longer it takes.");
        }
        else
        {
            Widgets.Coloured(Widgets.Done, $"All raid items in week {result.LastFinishWeek}.");
            Widgets.Tooltip("Every coffer and every upgrade material handed out. Tomestone pieces are " +
                            "the other half, and they are on their own clock.");
        }

        var whole = result.LastTomeFinishWeek;

        if (whole <= 0)
            return;

        ImGui.SameLine(0f, 8f);

        if (result.BeyondHorizon(whole))
        {
            Widgets.Coloured(Widgets.Bad, $"— full sets not inside {result.Horizon} weeks.");
        }
        else
        {
            ImGui.TextDisabled($"— full sets, tomestone gear included, in week {whole}.");
        }

        Widgets.Tooltip($"Everyone collects {tiers.Tier.TomestonesPerWeek} a week and started the " +
                        $"tier with {tiers.Tier.PriorTomeWeeks} week(s) banked. Clearing faster does " +
                        "not move this number — only buying less does.");
    }

    /// <summary>
    /// What is expected to drop next and who it goes to.
    ///
    /// Driven by the forecast's first week rather than computed separately, so this and week 1 of
    /// the schedule below cannot disagree — they are the same answer shown at two lengths.
    /// </summary>
    private void DrawNextDrops(SimulationResult result)
    {
        ImGui.TextUnformatted("Next drops");
        Widgets.HelpMarker("Every coffer the coming week can put up, in fight order, and who it is " +
                           "for. All four accessories are listed whether or not the tier expects " +
                           "four of them to drop — which two turn up is not something a drop rate " +
                           "can answer, and the point of this table is to know before it does.\n\n" +
                           "Each drop is decided with the ones above it already given away, so the " +
                           "same player is not named twice for a piece they can only wear once.");

        ImGui.SameLine();

        // How much of a list somebody wants to look at is theirs, whatever the server says they may
        // change. It is stored on this client for the same reason — shared, a reader's choice would
        // be undone by the next pull.
        var onlyNext = config.ShowOnlyNextRecipient;

        if (ImGui.Checkbox("Winner only", ref onlyNext))
        {
            config.ShowOnlyNextRecipient = onlyNext;
            config.Save();
        }

        Widgets.HelpMarker("Hides the runners-up, leaving just who each drop is for.");
        ImGui.Separator();

        // Drops only. What the coming week's books buy is a different kind of thing — nobody has to
        // be in the instance for it and nobody else was competing for it — and it belongs under
        // Planned book exchanges rather than mixed into a table about coffers.
        var week = result.Awards.Where(a => a is { Week: 1, Bought: false }).ToList();
        if (week.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing expected next week.");
            return;
        }

        var known = new HashSet<int>();

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            known.Add(encounter.Index);
            DrawFightDrops(encounter.Index, encounter.Name,
                           week.Where(a => a.Encounter == encounter.Index).ToList());
        }

        // Anything the tier no longer has a fight for still has to appear somewhere.
        foreach (var group in week.Where(a => !known.Contains(a.Encounter))
                                  .GroupBy(a => a.Encounter)
                                  .OrderBy(g => g.Key))
        {
            DrawFightDrops(group.Key, $"Fight #{group.Key}", group.ToList());
        }
    }

    /// <summary>
    /// One fight's drops, in a table of its own.
    ///
    /// A table per fight rather than a Fight column down the side. ImGui cannot span rows, and both
    /// ways of pretending otherwise were worse: repeating the name on every row buries the drops,
    /// and nesting a table beside the name never lines its columns up with the one above it. Here
    /// the fight is the first column's heading, which costs nothing and cannot drift.
    /// </summary>
    private void DrawFightDrops(int index, string name, IReadOnlyList<PlannedAward> awards)
    {
        using var id = ImRaii.PushId(index);

        if (awards.Count == 0)
        {
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            Widgets.Coloured(Widgets.Muted, "— nothing expected");
            return;
        }

        using var table = ImRaii.Table("##drops", 2,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Goes to");
        ImGui.TableHeadersRow();

        foreach (var award in awards)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(award.What);

            ImGui.TableNextColumn();
            DrawAwardRecipients(award);
        }
    }

    /// <summary>
    /// The winner and who else was in the running — both taken from the award itself.
    ///
    /// It used to name the winner from the week's calculation and list the runners-up from a fresh
    /// one that had no idea what the earlier drops of the same week had already done. The two
    /// disagreed often enough to look broken, because they were two answers to different questions
    /// shown as one line.
    /// </summary>
    private void DrawAwardRecipients(PlannedAward award)
    {
        var ranking = award.Considered ?? [];

        Widgets.Coloured(Widgets.Done, award.PlayerName);

        if (!string.IsNullOrEmpty(award.Why))
            Widgets.Tooltip($"{award.PlayerName}\n{award.Why}");

        if (config.ShowOnlyNextRecipient || ranking.Count <= 1)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled($"> {string.Join(" > ", ranking.Skip(1).Take(3).Select(c => c.Name))}");
        Widgets.Tooltip(string.Join("\n", ranking.Select((c, i) => $"{i + 1}. {c.Name} — {c.Reason}")));
    }

    /// <summary>
    /// The rest of the tier, one tab per week.
    ///
    /// The weeks were collapsing headers, and beside them a second tab listing every book purchase
    /// of the whole tier. Both went once a week could be read whole: the fights in order and then
    /// what the week's books buy is the sequence the group actually lives through, and a list of
    /// every purchase across twelve weeks answers a question nobody was asking.
    /// </summary>
    private void DrawSchedule(SimulationResult result)
    {
        ImGui.TextUnformatted("Expected schedule");
        Widgets.HelpMarker("The whole tier played forward: every fight cleared every week, every " +
                           "coffer handed to whoever the rules put first, and what the week's books " +
                           "buy at the end of it.\n\n" +
                           "Everyone earns one book from every fight each week on top of what they " +
                           "are already holding, and the last fight's books are traded down where the " +
                           "tier allows it and a purchase needs it.");
        ImGui.Separator();

        if (result.Awards.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing left to hand out.");
            return;
        }

        var last = result.Awards.Max(a => a.Week);

        // Scrolling rather than shrinking: a tier can run past the tab bar's width, and "W…" tabs
        // would be a worse answer than an arrow.
        using var tabs = ImRaii.TabBar("##weeks", ImGuiTabBarFlags.FittingPolicyScroll);
        if (!tabs.Success)
            return;

        for (var week = 1; week <= last; week++)
        {
            using var tab = ImRaii.TabItem($"Week {week}");
            if (tab.Success)
                DrawWeek(result, week);
        }
    }

    private void DrawWeek(SimulationResult result, int week)
    {
        using var indent = ImRaii.PushIndent();

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            var awards = result.Awards
                               .Where(a => a.Week == week && a.Encounter == encounter.Index && !a.Bought)
                               .ToList();

            ImGui.TextUnformatted(encounter.Name);

            using var inner = ImRaii.PushIndent();

            // Every fight is listed every week, including the ones handing out nothing — "this
            // fight gives you nothing next week" is worth being able to see.
            if (awards.Count == 0)
            {
                Widgets.Coloured(Widgets.Muted, "nothing");
                continue;
            }

            foreach (var award in awards)
                ImGui.TextUnformatted($"{award.What}  →  {award.PlayerName}");
        }

        DrawWeeksExchanges(result, week);
    }

    /// <summary>
    /// The week's purchases, gathered at the end of it rather than filed under the fight whose
    /// books pay for them.
    ///
    /// Filing them by fight put them where the books come from, which is true and unhelpful: a fight
    /// heading in this list means "go and clear this", and a purchase is not that. Nobody has to be
    /// anywhere for it except an NPC, and it happens once the week's clears are done — which is
    /// exactly where it now sits.
    /// </summary>
    private void DrawWeeksExchanges(SimulationResult result, int week)
    {
        var bought = result.Awards.Where(a => a.Week == week && a.Bought).ToList();

        Widgets.Coloured(Widgets.Augment, "Exchange");

        using var inner = ImRaii.PushIndent();

        if (bought.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "nothing");
            return;
        }

        var tier = tiers.Tier;

        foreach (var award in bought)
        {
            // Which shop it came from, on the line rather than only in the price: a week that hands
            // somebody a body piece twice is two different purchases, and the reader has to be able
            // to see that at a glance rather than by reading the numbers after them.
            var what = award.WithTomestones ? $"{award.What} (tomes)" : $"{award.What} (books)";

            ImGui.TextUnformatted($"{what}  →  {award.PlayerName}");

            ImGui.SameLine();
            ImGui.TextDisabled(CostOf(tier, award));

            if (award.Traded is { } trade)
            {
                var source = tier.Encounter(trade.FromEncounter)?.Name ?? $"#{trade.FromEncounter}";

                Widgets.Tooltip($"{trade.Covered} of them come from trading in {trade.Books} × " +
                                $"{source}, which this player has spare.");
            }
        }
    }

    /// <summary>What a purchase costs, including the part that has to be traded for first.</summary>
    private static string CostOf(TierDefinition tier, PlannedAward award)
    {
        // Two shops, two currencies. A tomestone purchase has no fight behind it and no books to
        // trade, so it says its price and stops there.
        if (award.WithTomestones)
            return $"{award.TomeCost:N0} tomestones";

        var cost = award.Upgrade != null
                       ? tier.CostForUpgrade(award.Upgrade.Value)
                       : award.Slot != null
                           ? tier.CostForSlot(award.Slot.Value)
                           : null;

        var fight = tier.Encounter(award.Encounter)?.Name ?? $"#{award.Encounter}";
        var text = cost == null ? $"{fight} books" : $"{cost.Cost} × {fight}";

        if (award.Traded is not { } trade)
            return text;

        var source = tier.Encounter(trade.FromEncounter)?.Name ?? $"#{trade.FromEncounter}";
        return $"{text} (exchange {trade.Books} × {source})";
    }

    public void Dispose() { }
}
