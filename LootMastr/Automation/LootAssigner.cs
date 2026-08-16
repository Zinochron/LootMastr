using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr.Automation;

/// <summary>One row of the loot window with the planner's answer attached.</summary>
public sealed record LootDecision(
    LiveLootItem Item,
    RosterMember? Winner,
    string Reason,
    IReadOnlyList<Candidate> Ranking);

/// <summary>
/// Works out who each item in the open loot window should go to, and is the single place that would
/// ever act on it.
///
/// The deciding is finished; the acting is not. See <see cref="PerformAssignment"/>.
/// </summary>
public sealed class LootAssigner
{
    private readonly Configuration config;
    private readonly LootWindowReader loot;
    private readonly LootPlanner planner;
    private readonly RosterStore roster;
    private readonly SafetyGuard guard;
    private readonly TierCatalog tiers;
    private readonly LootAssignmentRunner runner;

    private string lastSignature = string.Empty;
    private List<LootDecision> decisions = [];

    public LootAssigner(Configuration config, LootWindowReader loot, LootPlanner planner,
                        RosterStore roster, SafetyGuard guard, TierCatalog tiers,
                        LootAssignmentRunner runner)
    {
        this.config = config;
        this.loot = loot;
        this.planner = planner;
        this.roster = roster;
        this.guard = guard;
        this.tiers = tiers;
        this.runner = runner;
    }

    public IReadOnlyList<LootDecision> Decisions => decisions;

    public string Status { get; private set; } = string.Empty;

    /// <summary>
    /// Recomputes only when the window's contents actually changed. Each decision runs one
    /// simulation per candidate, which is cheap but not something to do sixty times a second.
    /// </summary>
    public void Refresh(bool force = false)
    {
        if (!loot.WindowOpen)
        {
            if (decisions.Count > 0)
            {
                decisions = [];
                lastSignature = string.Empty;
            }

            return;
        }

        var items = loot.Read();

        // The roster is part of the signature, not just the window. Inside an instance the chest
        // does not change, so keying only on it meant that removing someone from the roster left
        // the old ranking standing — with the removed player still in it.
        var signature = string.Join("|", items.Select(i => $"{i.Index}:{i.ItemId}:{i.RollResult}")) +
                        $"#{roster.Signature()}";

        if (!force && signature == lastSignature)
            return;

        lastSignature = signature;
        decisions = items.Select(Decide).ToList();

        LearnTerritory(items);
    }

    private LootDecision Decide(LiveLootItem item)
    {
        if (!item.IsTierLoot)
            return new LootDecision(item, null, "Not part of this tier — free for all.", []);

        var ranking = item.Upgrade != null
                          ? planner.RankForUpgrade(item.Upgrade.Value)
                          : planner.RankForSlot(item.Slot!.Value);

        if (ranking.Count == 0)
            return new LootDecision(item, null, "Nobody in the roster still needs it — greed.", ranking);

        var best = ranking[0];
        return new LootDecision(item, best.Member, best.Reason, ranking);
    }

    /// <summary>
    /// Ties the zone to a fight the first time its chest is seen, so clearing a fight can later
    /// count everyone's book without anyone having said which fight it was.
    /// </summary>
    private void LearnTerritory(IReadOnlyList<LiveLootItem> items)
    {
        var territory = Services.ClientState.TerritoryType;
        if (territory == 0)
            return;

        foreach (var encounter in tiers.Tier.Encounters)
        {
            var matches = items.Count(i =>
                                          (i.Slot != null && encounter.DropSlots.Contains(Slots.CofferSlot(i.Slot.Value))) ||
                                          (i.Upgrade != null && encounter.UpgradeDrops.Contains(i.Upgrade.Value)));

            // Two matching drops is enough to be sure; one could be a slot two fights share.
            if (matches >= 2)
            {
                tiers.LearnTerritory(encounter.Index, territory);
                return;
            }
        }
    }

    public GuardVerdict Verdict => guard.CheckAssign();

    /// <summary>
    /// Marks a decision as carried out by hand: the leader assigned it in the game window
    /// themselves. Ticks the need list and counts the item towards fairness.
    /// </summary>
    public void ConfirmByHand(LootDecision decision)
    {
        if (decision.Winner == null)
            return;

        Record(decision.Winner, decision.Item);
        Status = $"{decision.Item.What} recorded for {decision.Winner.Name}.";
    }

    /// <summary>Ticks off what a player received, whether it was assigned here or noticed in chat.</summary>
    public void Record(RosterMember member, LiveLootItem item)
    {
        if (item.Upgrade != null)
        {
            foreach (var slot in Slots.All)
            {
                var need = member.NeedFor(slot);
                if (need.Source != GearSource.TomeAugmented || need.UpgradeObtained)
                    continue;

                if (Slots.SideOf(slot) != item.Upgrade.Value)
                    continue;

                need.UpgradeObtained = true;
                member.ItemsReceived++;
                config.Save();
                return;
            }

            return;
        }

        if (item.Slot == null)
            return;

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);
            if (need.Source != GearSource.Raid || need.Obtained)
                continue;

            if (Slots.CofferSlot(slot) != Slots.CofferSlot(item.Slot.Value))
                continue;

            need.Obtained = true;
            member.ItemsReceived++;
            config.Save();
            return;
        }
    }

    /// <summary>
    /// Hands one item over, through <see cref="LootAssignmentRunner"/>. One at a time: each
    /// assignment walks three windows and the next cannot start until those are clear.
    /// </summary>
    public bool PerformAssignment(LootDecision decision, out string reason)
    {
        if (decision.Winner == null)
        {
            reason = "No planned recipient for that item.";
            return false;
        }

        reason = runner.Start(decision.Item, decision.Winner.Name);
        return runner.IsRunning;
    }

    public bool IsAssigning => runner.IsRunning;

    public string RunnerStatus => runner.Status;

    public void StopAssigning(string reason) => runner.Stop(reason);
}
