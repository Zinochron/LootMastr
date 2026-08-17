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
    }

    public void Draw()
    {
        if (roster.Members.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Add players to the roster first.");
            return;
        }

        if (ImGui.Button("Recalculate"))
            Invalidate();

        Widgets.HelpMarker("Recalculates on its own whenever the roster, a tick or a book count " +
                           "changes; this is only here for after a tier edit. Weeks are counted from " +
                           "now, so week 1 is the next reset.");

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

        coming ??= planner.ComingWeek();
        schedule ??= planner.Schedule();

        ImGuiHelpers.ScaledDummy(6f);
        DrawForecast(schedule);

        ImGuiHelpers.ScaledDummy(10f);
        DrawNextDrops(coming);

        ImGuiHelpers.ScaledDummy(10f);
        DrawSchedule(schedule);
    }

    private void DrawForecast(SimulationResult result)
    {
        ImGui.TextUnformatted("Who finishes when");
        ImGui.Separator();

        if (result.BeyondHorizon(result.LastFinishWeek))
        {
            Widgets.Coloured(Widgets.Wanted,
                             $"Not everyone is done within {result.Horizon} weeks. Raise the horizon in " +
                             "Settings to see how much longer it takes.");
        }
        else
        {
            ImGui.TextUnformatted($"Everyone is done in week {result.LastFinishWeek}.");
        }

        using var table = ImRaii.Table("##forecast", 4,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Still needs", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Done", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var plans = planner.BuildPlans().ToDictionary(p => p.Key);

        foreach (var member in roster.Members)
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
        }
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
    /// The rest of the tier, week by week, split in two.
    ///
    /// Drops and book purchases were one list with the bought ones marked "(books)", and they are
    /// not the same kind of thing: a coffer is a decision made in the instance with seven other
    /// people wanting it, and an exchange is one player walking to an NPC. Reading a week meant
    /// filtering the two apart by eye every time.
    /// </summary>
    private void DrawSchedule(SimulationResult result)
    {
        if (result.Awards.Count == 0)
        {
            ImGui.TextUnformatted("Expected schedule");
            ImGui.Separator();
            Widgets.Coloured(Widgets.Muted, "Nothing left to hand out.");
            return;
        }

        using var tabs = ImRaii.TabBar("##schedule");
        if (!tabs.Success)
            return;

        using (var drops = ImRaii.TabItem("Expected schedule"))
        {
            if (drops.Success)
            {
                Widgets.HelpMarker("The whole tier played forward: every fight cleared every week, " +
                                   "every coffer handed to whoever the rules put first, and what " +
                                   "the week's books buy at the end of it.");

                DrawScheduleWeeks(result);
            }
        }

        using var exchanges = ImRaii.TabItem("Planned book exchanges");
        if (exchanges.Success)
        {
            Widgets.HelpMarker("Every piece the plan expects to be bought rather than won, in the " +
                               "week the books for it are there.\n\n" +
                               "Everyone earns one book from every fight each week on top of what " +
                               "they are already holding, and the last fight's books are traded down " +
                               "where the tier allows it and a purchase needs it.");

            DrawExchangeWeeks(result);
        }
    }

    private void DrawScheduleWeeks(SimulationResult result)
    {
        // Its own id scope, or the two tabs' week headers share an open/closed state.
        using var id = ImRaii.PushId("drops");

        var encounters = tiers.Tier.Encounters.OrderBy(e => e.Index).ToList();

        foreach (var week in Weeks(result.Awards))
        {
            using var indent = ImRaii.PushIndent();

            foreach (var encounter in encounters)
            {
                var awards = result.Awards
                                   .Where(a => a.Week == week && a.Encounter == encounter.Index && !a.Bought)
                                   .ToList();

                ImGui.TextUnformatted(encounter.Name);

                using var inner = ImRaii.PushIndent();

                // Every fight is listed every week, including the ones handing out nothing —
                // "this fight gives you nothing next week" is worth being able to see.
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

        Widgets.Coloured(Widgets.Augment, "Book exchange");

        using var inner = ImRaii.PushIndent();

        if (bought.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "nothing");
            return;
        }

        var tier = tiers.Tier;

        foreach (var award in bought)
        {
            ImGui.TextUnformatted($"{award.What}  →  {award.PlayerName}");

            ImGui.SameLine();
            ImGui.TextDisabled(CostOf(tier, award));
        }
    }

    /// <summary>What a purchase costs, including the part that has to be traded for first.</summary>
    private static string CostOf(TierDefinition tier, PlannedAward award)
    {
        var text = PriceOf(tier, award);

        if (award.Traded is not { } trade)
            return text;

        var source = tier.Encounter(trade.FromEncounter)?.Name ?? $"#{trade.FromEncounter}";
        return $"{text} (exchange {trade.Books} × {source})";
    }

    /// <summary>Books and fight, without the trade. Shared so the two views cannot price differently.</summary>
    private static string PriceOf(TierDefinition tier, PlannedAward award)
    {
        var cost = award.Upgrade != null
                       ? tier.CostForUpgrade(award.Upgrade.Value)
                       : award.Slot != null
                           ? tier.CostForSlot(award.Slot.Value)
                           : null;

        var fight = tier.Encounter(award.Encounter)?.Name ?? $"#{award.Encounter}";
        return cost == null ? $"{fight} books" : $"{cost.Cost} × {fight}";
    }

    private void DrawExchangeWeeks(SimulationResult result)
    {
        using var id = ImRaii.PushId("buys");

        var bought = result.Awards.Where(a => a.Bought).ToList();

        if (bought.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing is expected to be bought with books.");
            return;
        }

        var tier = tiers.Tier;

        foreach (var week in Weeks(bought))
        {
            using var indent = ImRaii.PushIndent();
            using var table = ImRaii.Table($"##buys{week}", 3,
                                           ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (!table.Success)
                continue;

            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Buys", ImGuiTableColumnFlags.WidthFixed, 150f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Costs");
            ImGui.TableHeadersRow();

            foreach (var award in bought.Where(a => a.Week == week))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(award.PlayerName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(award.What);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(PriceOf(tier, award));

                // Say when part of it has to be traded for first. "Three second-fight books" is a
                // different instruction depending on whether you have three, and nothing in the
                // counts says so.
                if (award.Traded is not { } trade)
                    continue;

                var source = tier.Encounter(trade.FromEncounter)?.Name ?? $"#{trade.FromEncounter}";

                ImGui.SameLine();
                Widgets.Coloured(Widgets.Augment, $"(exchange {trade.Books} × {source})");

                Widgets.Tooltip($"{trade.Covered} of them come from trading in {trade.Books} × " +
                                $"{source}, which this player has spare.");
            }
        }
    }

    /// <summary>
    /// Week headers, yielding only the ones the reader opened. Weeks fold away because the list gets
    /// long; what is inside one does not, because a week is only worth opening to see all at once.
    /// </summary>
    private static IEnumerable<int> Weeks(IReadOnlyList<PlannedAward> awards)
    {
        var last = awards.Max(a => a.Week);

        for (var week = 1; week <= last; week++)
        {
            var open = week == 1 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

            if (ImGui.CollapsingHeader($"Week {week}###week{week}", open))
                yield return week;
        }
    }

    public void Dispose() { }
}
