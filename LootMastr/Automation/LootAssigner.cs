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
    IReadOnlyList<Candidate> Ranking,
    bool AlreadyAssigned = false,
    bool Offered = false);

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

    /// <summary>
    /// Items handed over since this chest opened, keyed by their slot and id.
    ///
    /// Nothing in the loot window answers this. In Lootmaster mode <c>Loot.Items</c> is empty, so
    /// every item reads as undecided however many have been given away — and the chest itself keeps
    /// showing an awarded coffer, so an item disappearing is not a signal either. What fills this is
    /// the obtain line in chat, which is also the only thing that can tell an award apart from the
    /// game refusing a unique item the recipient already owns.
    /// </summary>
    private readonly HashSet<string> assigned = [];

    /// <summary>The row last put in front of the game, still waiting for the chat line to settle it.</summary>
    private string? offeredKey;

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
        CollectOffered();

        if (!loot.WindowOpen)
        {
            if (decisions.Count > 0)
            {
                decisions = [];
                lastSignature = string.Empty;
                offeredKey = null;

                // A new chest is a clean slate; what was handed out of the last one is history.
                assigned.Clear();
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

        // Worked out in order rather than item by item. A chest can hold the same coffer twice —
        // the recorded Deltascape chest had two of them — and since the gear is unique the second
        // cannot go to whoever is taking the first. Each decision is made with the ones above it
        // already counted.
        var pending = new List<PendingAward>();
        decisions = [];

        foreach (var item in items)
        {
            var decision = Decide(item, pending);

            if (KeyOf(item) == offeredKey)
                decision = decision with { Offered = true };

            decisions.Add(decision);

            if (decision.Winner != null)
                pending.Add(new PendingAward(decision.Winner.Key, item.Slot, item.Upgrade));
        }

        LearnTerritory(items);
    }

    private static string KeyOf(LiveLootItem item) => $"{item.Index}:{item.ItemId}";

    /// <summary>
    /// Notes which row the runner just put in front of the game. Offered, not done: the game can
    /// still refuse it, and only chat says whether anyone actually received anything.
    /// </summary>
    private void CollectOffered()
    {
        if (runner.Offered is not { } item)
            return;

        offeredKey = KeyOf(item);
        runner.ClearOffered();
        lastSignature = string.Empty;
    }

    /// <summary>
    /// Chat saw a coffer change hands, so it is out of the running whoever got it — including
    /// somebody outside the roster.
    ///
    /// This is what stops a coffer being offered twice. The plugin's own click cannot: pressing the
    /// buttons is not the same as the item moving, and a unique coffer the recipient already owns
    /// comes back with an error dialog and stays exactly where it was.
    /// </summary>
    public void MarkHandedOver(uint itemId)
    {
        var row = decisions.FirstOrDefault(d => d.Item.ItemId == itemId && !d.AlreadyAssigned);
        if (row == null)
            return;

        var key = KeyOf(row.Item);
        assigned.Add(key);

        if (offeredKey == key)
            offeredKey = null;

        lastSignature = string.Empty;
    }

    private LootDecision Decide(LiveLootItem item, IReadOnlyList<PendingAward> pending)
    {
        if (assigned.Contains(KeyOf(item)))
            return new LootDecision(item, null, "Already handed over.", [], AlreadyAssigned: true);

        if (!item.IsTierLoot)
            return new LootDecision(item, null, "Not part of this tier — free for all.", []);

        var ranking = item.Upgrade != null
                          ? planner.RankForUpgrade(item.Upgrade.Value, pending)
                          : planner.RankForSlot(item.Slot!.Value, pending);

        if (ranking.Count == 0)
        {
            var reason = pending.Any(p => p.Slot == item.Slot && p.Upgrade == item.Upgrade)
                             ? "Nobody left who needs it — the other one in this chest covers them."
                             : "Nobody in the roster still needs it — greed.";

            return new LootDecision(item, null, reason, ranking);
        }

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

        // Also out of the running: recording it by hand means it has been handed over.
        var key = KeyOf(decision.Item);
        assigned.Add(key);

        if (offeredKey == key)
            offeredKey = null;

        lastSignature = string.Empty;

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

    /// <summary>
    /// Hands over an item the ranking never produced a winner for.
    ///
    /// The other overload refuses when <c>Winner</c> is null, and that is right for a coffer — a
    /// recipient nobody chose is a bug. For a stone or a mount there is no winner by construction:
    /// somebody picked a name in the Loot tab, and that is the whole decision.
    /// </summary>
    public bool PerformAssignment(LiveLootItem item, string playerName, out string reason)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            reason = "Nobody selected.";
            return false;
        }

        reason = runner.Start(item, playerName);
        return runner.IsRunning;
    }

    /// <summary>
    /// Ticks off one of the three drops that have no slot, and takes it out of the chest's running.
    ///
    /// Recorded on the member rather than on a need, because none of these is a need. The weapon
    /// stone in particular has to be remembered for two separate reasons: it puts 500 tomestones on
    /// that player's bill, and the material that augments what it buys is only useful to somebody
    /// who took one.
    /// </summary>
    public void RecordSpecial(LiveLootItem item, RosterMember member, SpecialDrop kind)
    {
        switch (kind)
        {
            case SpecialDrop.WeaponToken:
                member.WeaponTokenObtained = true;
                break;

            case SpecialDrop.WeaponAugment:
                member.WeaponAugmentObtained = true;
                break;

            case SpecialDrop.Mount:
                member.MountObtained = true;
                break;
        }

        // Index -1 is the chat path: no row to take out of the running, and MarkHandedOver has
        // already done that from the item id.
        if (item.Index >= 0)
        {
            var key = KeyOf(item);
            assigned.Add(key);

            if (offeredKey == key)
                offeredKey = null;
        }

        lastSignature = string.Empty;
        config.Save();

        Status = $"{(string.IsNullOrEmpty(item.Name) ? kind.ToString() : item.Name)} recorded for {member.Name}.";
    }

    /// <summary>
    /// Puts an item up for greed and takes it out of the plugin's running.
    ///
    /// Out of the running rather than handed over: greeding settles who the plugin should suggest,
    /// and settles nothing at all about who ends up with the thing. That is what a roll is for.
    /// </summary>
    public bool SetGreedOnly(LiveLootItem item, out string reason)
    {
        reason = runner.Greed(item);

        if (!reason.EndsWith("put up for greed.", StringComparison.Ordinal))
            return false;

        assigned.Add(KeyOf(item));
        lastSignature = string.Empty;

        return true;
    }

    public bool IsAssigning => runner.IsRunning;

    public string RunnerStatus => runner.Status;

    public void StopAssigning(string reason) => runner.Stop(reason);
}
