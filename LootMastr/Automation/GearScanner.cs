using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Automation;

/// <summary>
/// Reads what everyone is actually wearing: the local player from the equipped container, the rest
/// of the party through the examine window, one at a time.
///
/// Runs as a state machine on the framework tick rather than as a loop, because examining is a
/// request the game answers a few frames later and the answer has to be waited for. Nothing here
/// judges success by a return value — each step waits for the window to actually be showing the
/// character it asked for, which is the lesson <c>Sortr</c> paid for.
///
/// The one rule that matters: a scan may set <see cref="SlotNeed.Obtained"/> but never clears it.
/// Not wearing a piece is no evidence of not owning it — the coffer may simply be unopened — and
/// clearing it would put an already-awarded slot back into the distribution.
/// </summary>
public sealed class GearScanner : IDisposable
{
    private const int StepTimeoutMs = 6000;

    private enum Phase
    {
        Idle,
        Local,
        Request,
        Await,
        Finished,
    }

    /// <summary>How long to wait after landing in a duty before reading anyone.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(8);

    /// <summary>How long to wait again when the settle timer lands mid-pull.</summary>
    private static readonly TimeSpan CombatRetry = TimeSpan.FromSeconds(15);

    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly JobCatalog jobs;
    private readonly PartyReader party;
    private readonly EquipmentReader equipment;
    private readonly AttributeReader attributes;
    private readonly GearClassifier classifier;

    private readonly Queue<PartyPlayer> queue = new();
    private readonly List<string> skipped = [];

    /// <summary>
    /// Read, but without their attributes — the half-success the status line used to swallow.
    ///
    /// Their gear is on the row and their damage cannot be estimated, and those two facts sit in
    /// different places in the UI. Naming them here is the only moment the connection is obvious.
    /// </summary>
    private readonly List<string> withoutStats = [];

    private Phase phase = Phase.Idle;
    private PartyPlayer current;
    private DateTime stepStarted;
    private DateTime lastAction = DateTime.MinValue;
    private int scanned;
    private bool readLocal;

    /// <summary>Territory we still owe an automatic scan, and when to try it. 0 means none.</summary>
    private uint pendingTerritory;
    private DateTime pendingAt;

    public GearScanner(Configuration config, RosterStore roster, JobCatalog jobs, PartyReader party,
                       EquipmentReader equipment, AttributeReader attributes, GearClassifier classifier)
    {
        this.config = config;
        this.roster = roster;
        this.jobs = jobs;
        this.party = party;
        this.equipment = equipment;
        this.attributes = attributes;
        this.classifier = classifier;

        Services.Framework.Update += OnUpdate;
        Services.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public bool IsRunning => phase != Phase.Idle;

    public string Status { get; private set; } = string.Empty;

    /// <summary>Everyone in the party, whether or not the roster knows them. The manual button.</summary>
    public string Start() => Begin(party.Read(), local: true);

    /// <summary>
    /// Everyone in the party the roster knows and whose current job is the role the roster expects.
    /// This is what runs on its own after entering a duty.
    ///
    /// The role check is the whole safety of doing this unasked. A static's tank turning up on a
    /// damage job for a farm run is normal, and writing that gear onto their tank row would quietly
    /// wreck the plan — so a role that does not match is skipped and said out loud, never applied.
    /// </summary>
    public string StartForRoster()
    {
        var wanted = new List<PartyPlayer>();
        var mismatched = new List<string>();

        foreach (var player in party.Read())
        {
            var member = roster.Find(player.Name, player.World);
            if (member == null)
                continue;

            var actual = jobs.RoleOf(player.JobId);
            var expected = roster.RoleOf(member);

            if (actual != expected)
            {
                mismatched.Add($"{player.Name} (on a {actual} job, roster says {expected})");
                continue;
            }

            wanted.Add(player);
        }

        if (wanted.Count == 0)
        {
            // Nothing to read and nobody skipped is not worth a message. Zoning into a dungeon
            // alone would otherwise leave a complaint sitting on the Roster tab.
            if (mismatched.Count == 0)
                return "Nobody in the party is in the roster.";

            Status = $"Nobody to read; skipped {string.Join(", ", mismatched)}.";
            return Status;
        }

        var result = Begin(wanted, local: wanted.Any(p => p.IsLocalPlayer));

        // Only onto a list this run actually cleared — a refused start leaves the last run's alone.
        if (IsRunning)
            skipped.AddRange(mismatched);

        return result;
    }

    /// <summary>One player, for the button on their own sheet.</summary>
    public string StartFor(RosterMember member)
    {
        var player = party.Read().FirstOrDefault(p => roster.Find(p.Name, p.World) == member);

        if (string.IsNullOrEmpty(player.Name))
            return $"{member.Name} is not in the party.";

        return Begin([player], local: player.IsLocalPlayer);
    }

    private string Begin(IEnumerable<PartyPlayer> targets, bool local)
    {
        if (IsRunning)
            return "Already scanning.";

        if (!Services.ClientState.IsLoggedIn)
            return "Not logged in.";

        if (Services.Condition[ConditionFlag.InCombat])
            return "Not while in combat.";

        queue.Clear();
        skipped.Clear();
        withoutStats.Clear();
        scanned = 0;
        readLocal = local;

        foreach (var player in targets)
        {
            if (player.IsLocalPlayer)
                continue;

            if (!player.IsPresent)
            {
                skipped.Add($"{player.Name} (not in this zone)");
                continue;
            }

            queue.Enqueue(player);
        }

        phase = Phase.Local;
        stepStarted = DateTime.UtcNow;
        Status = readLocal ? "Reading your own gear…" : "Reading gear…";
        return Status;
    }

    /// <summary>
    /// Arms the automatic scan. Not run here: the party list is not populated the instant the zone
    /// changes, and the condition flags lag it too, so this only writes down that one is owed.
    /// </summary>
    private void OnTerritoryChanged(uint territory)
    {
        pendingTerritory = territory;
        pendingAt = DateTime.UtcNow + SettleDelay;
    }

    private void CheckPendingScan()
    {
        if (pendingTerritory == 0 || DateTime.UtcNow < pendingAt)
            return;

        // Left again while waiting — whatever was owed was owed to a zone we are no longer in.
        if (Services.ClientState.TerritoryType != pendingTerritory)
        {
            pendingTerritory = 0;
            return;
        }

        if (!config.ExpertMode || !config.AutoReadGearOnEnter || !Services.Condition[ConditionFlag.BoundByDuty])
        {
            pendingTerritory = 0;
            return;
        }

        // Landed in a pull. Wait it out rather than giving up — the interesting duties start fast.
        if (Services.Condition[ConditionFlag.InCombat])
        {
            pendingAt = DateTime.UtcNow + CombatRetry;
            return;
        }

        pendingTerritory = 0;

        var result = StartForRoster();

        if (config.VerboseChat || IsRunning)
            Services.Chat.Print($"LootMastr: {result}");
    }

    public void Stop(string reason)
    {
        if (phase == Phase.Idle)
            return;

        CloseExamine();
        phase = Phase.Idle;
        queue.Clear();
        Status = reason;
    }

    private void OnUpdate(IFramework framework)
    {
        if (phase is Phase.Idle)
        {
            CheckPendingScan();
            return;
        }

        if (!Services.ClientState.IsLoggedIn)
        {
            Stop("Stopped — logged out.");
            return;
        }

        switch (phase)
        {
            case Phase.Local:
                ScanLocal();
                break;

            case Phase.Request:
                RequestNext();
                break;

            case Phase.Await:
                AwaitData();
                break;

            case Phase.Finished:
                Finish();
                break;
        }
    }

    private void ScanLocal()
    {
        var local = readLocal ? party.Read().FirstOrDefault(p => p.IsLocalPlayer) : default;

        if (!string.IsNullOrEmpty(local.Name))
        {
            var member = roster.Find(local.Name, local.World);
            if (member != null)
            {
                // Worked out from the items: the examine window hands one over for anybody else, and
                // nothing does for you. Passing 0 here is what put "i0" on your own sheet.
                var gear = equipment.ReadLocal();
                var hasStats = attributes.TryReadLocal(out var measured);

                Apply(member, gear, equipment.ReadLocalMelds(), equipment.AverageItemLevel(gear),
                      hasStats ? measured : null);

                scanned++;

                if (!hasStats)
                    withoutStats.Add(local.Name);
            }
        }

        phase = Phase.Request;
    }

    private unsafe void RequestNext()
    {
        if (queue.Count == 0)
        {
            phase = Phase.Finished;
            return;
        }

        // Menu-driven requests need spacing, and the game keeps its own examine cooldown on top.
        if ((DateTime.UtcNow - lastAction).TotalMilliseconds < config.ActionDelayMs)
            return;

        // The game keeps its own examine cooldown; asking through it just gets ignored.
        var state = UIState.Instance();
        if (state != null && state->Inspect.RequestCooldown > 0f)
            return;

        current = queue.Dequeue();

        var agent = AgentInspect.Instance();
        if (agent == null)
        {
            Stop("Stopped — the examine window is not available.");
            return;
        }

        agent->ExamineCharacter(current.EntityId, false);

        lastAction = DateTime.UtcNow;
        stepStarted = DateTime.UtcNow;
        phase = Phase.Await;
        Status = $"Examining {current.Name}…";
    }

    private unsafe void AwaitData()
    {
        var agent = AgentInspect.Instance();

        // Success is the window actually showing the character that was asked for, with items in
        // it. The call itself reports nothing useful.
        if (agent != null && agent->CurrentEntityId == current.EntityId && agent->IsAddonShown())
        {
            var gear = equipment.ReadInspected();

            if (gear.Count > 0)
            {
                var hasStats = attributes.TryReadInspected(current.EntityId, out var measured);

                // The items land before the attributes do. Closing the window the moment the gear is
                // readable used to write the gear and no stats at all, which shows up much later and
                // somewhere else entirely: a row that says it was read three minutes ago, next to a
                // damage estimate that cannot be computed. So the wait runs to the step timeout, and
                // only then settles for what did arrive.
                if (!hasStats && (DateTime.UtcNow - stepStarted).TotalMilliseconds < StepTimeoutMs)
                    return;

                var member = roster.Find(current.Name, current.World);
                if (member != null)
                {
                    // Both of these only exist while the window is up. The melds come out of the
                    // examine inventory container rather than the agent, which carries none.
                    Apply(member, gear, equipment.ReadInspectedMelds(), equipment.InspectedItemLevel(),
                          hasStats ? measured : null);

                    scanned++;

                    if (!hasStats)
                    {
                        withoutStats.Add(current.Name);

                        // Which of the two it was matters, and only the log can say: no answer at
                        // all, or an answer belonging to the character examined before this one.
                        Services.Log.Warning(
                            $"GearScanner: no attributes for {current.Name} ({current.EntityId:X}) " +
                            "within the step timeout.");
                    }
                }

                CloseExamine();
                lastAction = DateTime.UtcNow;
                phase = Phase.Request;
                return;
            }
        }

        if ((DateTime.UtcNow - stepStarted).TotalMilliseconds < StepTimeoutMs)
            return;

        skipped.Add($"{current.Name} (no answer)");
        CloseExamine();
        lastAction = DateTime.UtcNow;
        phase = Phase.Request;
    }

    private void Finish()
    {
        phase = Phase.Idle;

        Status = $"Read {scanned} character(s).";

        if (skipped.Count > 0)
            Status += $" Skipped {string.Join(", ", skipped)}.";

        if (withoutStats.Count > 0)
            Status += $" No stats for {string.Join(", ", withoutStats)} — read them again to get a damage estimate.";
    }

    private static unsafe void CloseExamine()
    {
        var agent = AgentInspect.Instance();
        if (agent != null && agent->IsAddonShown())
            agent->HideAddon();
    }

    /// <summary>
    /// Writes one character's equipment onto their roster row. Slots the scan did not see are
    /// cleared, so taking a piece off shows up; the obtained flags are only ever turned on.
    /// </summary>
    private void Apply(RosterMember member, IReadOnlyDictionary<GearSlot, uint> gear,
                       IReadOnlyDictionary<GearSlot, List<uint>> melds, int itemLevel,
                       MeasuredStats? measured)
    {
        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            if (!gear.TryGetValue(slot, out var itemId))
            {
                need.EquippedItemId = 0;
                need.EquippedSource = GearSource.None;
                need.EquippedMateria = [];
                continue;
            }

            need.EquippedItemId = itemId;
            need.EquippedSource = classifier.Classify(itemId);
            need.EquippedMateria = melds.TryGetValue(slot, out var melded) ? [..melded] : [];
        }

        // A container that did not load reads as "no melds anywhere", which is indistinguishable from
        // a set with none. Recording which it was keeps the comparison from counting a target set's
        // materia against nothing and overstating every upgrade.
        member.MeldsKnown = melds.Count > 0;

        // Before anything is compared, not after. The game reports the ring pair in its own order and
        // a gear planner in whichever the set was built in, so a player wearing both target rings the
        // other way round would otherwise be read as wearing neither — and two finished slots would
        // stay in the distribution.
        member.AlignRings();

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            // Wearing it is proof of owning it, and catches everything picked up before the plugin
            // was ever opened. The reverse is not proof of anything, so nothing is turned off here.
            if (need.EquippedItemId == 0 || !need.IsWearingTarget)
                continue;

            if (need.Source == GearSource.Raid)
                need.Obtained = true;
            else if (need.Source == GearSource.TomeAugmented)
                need.UpgradeObtained = true;
        }

        member.LastScannedUtc = DateTime.UtcNow;

        if (itemLevel > 0)
            member.AverageItemLevel = itemLevel;

        // Kept only when the read worked. Half a stat block is worse than none: the damage model
        // would rather say "no estimate" than quietly rate somebody on a missing critical hit.
        if (measured is { IsUsable: true } stats)
        {
            member.Attributes = new Dictionary<uint, int>(stats.Values);
            member.MeasuredJobId = stats.JobId;
            member.MeasuredLevel = stats.Level;
        }

        config.Save();
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;
        Services.ClientState.TerritoryChanged -= OnTerritoryChanged;

        if (phase != Phase.Idle)
            CloseExamine();
    }
}
