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
/// What the rest of the tier looks like, and who should take each kind of drop. Recomputed on
/// demand rather than every frame: a simulation per candidate per slot is cheap but not free.
/// </summary>
public sealed class PlanTab : ITab
{
    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly LootPlanner planner;
    private readonly TierCatalog tiers;

    private SimulationResult? forecast;
    private int cachedSignature;
    private readonly Dictionary<GearSlot, IReadOnlyList<Candidate>> slotRankings = new();
    private readonly Dictionary<GearSide, IReadOnlyList<Candidate>> upgradeRankings = new();

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
        forecast = null;
        slotRankings.Clear();
        upgradeRankings.Clear();
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

        forecast ??= planner.Forecast();

        ImGuiHelpers.ScaledDummy(6f);
        DrawForecast(forecast);

        ImGuiHelpers.ScaledDummy(10f);
        DrawNextDrops(forecast);

        ImGuiHelpers.ScaledDummy(10f);
        DrawSchedule(forecast);

        ImGuiHelpers.ScaledDummy(10f);
        DrawPriorities();
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
        Widgets.HelpMarker("The coming week, in fight order. The same thing week 1 of the schedule " +
                           "says, with who else was in the running.");

        ImGui.SameLine();
        var onlyNext = config.ShowOnlyNextRecipient;
        if (ImGui.Checkbox("Winner only", ref onlyNext))
        {
            config.ShowOnlyNextRecipient = onlyNext;
            config.Save();
        }

        Widgets.HelpMarker("Hides the runners-up, leaving just who each drop is for.");
        ImGui.Separator();

        var week = result.Awards.Where(a => a.Week == 1).ToList();
        if (week.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing expected next week.");
            return;
        }

        using var table = ImRaii.Table("##nextDrops", 3,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Fight", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Drop", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Goes to");
        ImGui.TableHeadersRow();

        var namedFight = -1;

        foreach (var award in week.OrderBy(a => a.Encounter))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (award.Encounter != namedFight)
            {
                ImGui.TextUnformatted(tiers.Tier.Encounter(award.Encounter)?.Name ?? $"#{award.Encounter}");
                namedFight = award.Encounter;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(award.What);

            if (award.Bought)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(books)");
            }

            ImGui.TableNextColumn();
            DrawAwardRecipients(award);
        }
    }

    private void DrawAwardRecipients(PlannedAward award)
    {
        var ranking = award.Upgrade != null
                          ? Upgrade(award.Upgrade.Value)
                          : award.Slot != null
                              ? Slot(award.Slot.Value)
                              : [];

        Widgets.Coloured(Widgets.Done, award.PlayerName);

        var winner = ranking.FirstOrDefault(c => c.Member.Name == award.PlayerName);
        if (winner != null)
            Widgets.Tooltip($"{winner.Member.Name}\n{winner.Reason}");

        if (config.ShowOnlyNextRecipient || ranking.Count <= 1)
            return;

        var rest = ranking.Where(c => c.Member.Name != award.PlayerName).Take(3).ToList();
        if (rest.Count == 0)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled($"> {string.Join(" > ", rest.Select(c => c.Member.Name))}");
        Widgets.Tooltip(string.Join("\n", ranking.Select((c, i) => $"{i + 1}. {c.Member.Name} — {c.Reason}")));
    }

    private IReadOnlyList<Candidate> Slot(GearSlot slot)
    {
        if (!slotRankings.TryGetValue(slot, out var ranking))
            slotRankings[slot] = ranking = planner.RankForSlot(slot);

        return ranking;
    }

    private IReadOnlyList<Candidate> Upgrade(GearSide side)
    {
        if (!upgradeRankings.TryGetValue(side, out var ranking))
            upgradeRankings[side] = ranking = planner.RankForUpgrade(side);

        return ranking;
    }

    /// <summary>
    /// The answer to "this just dropped, who takes it" for every kind of drop in the tier, computed
    /// up front so the call during a raid is a glance rather than a wait.
    /// </summary>
    private void DrawPriorities()
    {
        ImGui.TextUnformatted("If it dropped right now");
        Widgets.HelpMarker("Every kind of drop the tier has, not just next week's — for when " +
                           "something comes up that the forecast did not expect.");
        ImGui.Separator();

        // Two levels of table rather than one. ImGui has no row spanning, so the fight name gets a
        // cell of its own with the whole group nested beside it — which is what spanning would look
        // like, and keeps the fight from being repeated down the column.
        using var table = ImRaii.Table("##priorities", 2,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Fight", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Drops");
        ImGui.TableHeadersRow();

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            using var id = ImRaii.PushId(encounter.Index);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(encounter.Name);

            ImGui.TableNextColumn();

            if (encounter.DropSlots.Count == 0 && encounter.UpgradeDrops.Count == 0)
            {
                Widgets.Coloured(Widgets.Muted, "nothing set up — see the Tier tab");
                continue;
            }

            using var inner = ImRaii.Table("##drops", 2,
                                           ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (!inner.Success)
                continue;

            ImGui.TableSetupColumn("##drop", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("##priority");

            foreach (var slot in encounter.DropSlots)
            {
                DrawPriorityRow(slot.CofferLabel(), Slot(slot));
            }

            foreach (var side in encounter.UpgradeDrops)
            {
                DrawPriorityRow($"{side} upgrade", Upgrade(side));
            }
        }
    }

    private static void DrawPriorityRow(string drop, IReadOnlyList<Candidate> ranking)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(drop);

        ImGui.TableNextColumn();

        if (ranking.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "nobody needs it — greed");
            return;
        }

        for (var i = 0; i < ranking.Count; i++)
        {
            var candidate = ranking[i];

            if (i > 0)
            {
                ImGui.SameLine(0f, 4f);
                ImGui.TextDisabled(">");
                ImGui.SameLine(0f, 4f);
            }

            var colour = i == 0 ? Widgets.Done : Widgets.Muted;
            Widgets.Coloured(colour, candidate.Member.Name);
            Widgets.Tooltip($"{candidate.Member.Name}\n{candidate.Reason}");
        }
    }

    private void DrawSchedule(SimulationResult result)
    {
        ImGui.TextUnformatted("Expected schedule");
        Widgets.HelpMarker("What the simulation expects to happen if every fight is cleared every week " +
                           "and coffers come up evenly. Books spent are marked.");
        ImGui.Separator();

        if (result.Awards.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing left to hand out.");
            return;
        }

        var encounters = tiers.Tier.Encounters.OrderBy(e => e.Index).ToList();
        var lastWeek = result.Awards.Max(a => a.Week);

        for (var week = 1; week <= lastWeek; week++)
        {
            // Weeks fold away because the list gets long; the fights inside one do not, because
            // a week is only worth opening to see all of it at once.
            var open = week == 1 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"Week {week}###week{week}", open))
                continue;

            using var indent = ImRaii.PushIndent();

            foreach (var encounter in encounters)
            {
                var awards = result.Awards
                                   .Where(a => a.Week == week && a.Encounter == encounter.Index)
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
                {
                    ImGui.TextUnformatted($"{award.What}  →  {award.PlayerName}");

                    if (!award.Bought)
                        continue;

                    ImGui.SameLine();
                    ImGui.TextDisabled("(books)");
                }
            }
        }
    }

    public void Dispose() { }
}
