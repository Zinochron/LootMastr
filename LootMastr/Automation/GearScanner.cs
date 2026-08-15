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

    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly PartyReader party;
    private readonly EquipmentReader equipment;
    private readonly GearClassifier classifier;

    private readonly Queue<PartyPlayer> queue = new();
    private readonly List<string> skipped = [];

    private Phase phase = Phase.Idle;
    private PartyPlayer current;
    private DateTime stepStarted;
    private DateTime lastAction = DateTime.MinValue;
    private int scanned;

    public GearScanner(Configuration config, RosterStore roster, PartyReader party,
                       EquipmentReader equipment, GearClassifier classifier)
    {
        this.config = config;
        this.roster = roster;
        this.party = party;
        this.equipment = equipment;
        this.classifier = classifier;

        Services.Framework.Update += OnUpdate;
    }

    public bool IsRunning => phase != Phase.Idle;

    public string Status { get; private set; } = string.Empty;

    public string Start()
    {
        if (IsRunning)
            return "Already scanning.";

        if (!Services.ClientState.IsLoggedIn)
            return "Not logged in.";

        if (Services.Condition[ConditionFlag.InCombat])
            return "Not while in combat.";

        queue.Clear();
        skipped.Clear();
        scanned = 0;

        foreach (var player in party.Read())
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
        Status = "Reading your own gear…";
        return Status;
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
            return;

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
        var local = party.Read().FirstOrDefault(p => p.IsLocalPlayer);

        if (!string.IsNullOrEmpty(local.Name))
        {
            var member = roster.Find(local.Name, local.World);
            if (member != null)
            {
                Apply(member, equipment.ReadLocal(), 0);
                scanned++;
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
                var member = roster.Find(current.Name, current.World);
                if (member != null)
                {
                    Apply(member, gear, equipment.InspectedItemLevel());
                    scanned++;
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

        Status = skipped.Count == 0
                     ? $"Read {scanned} character(s)."
                     : $"Read {scanned} character(s); skipped {string.Join(", ", skipped)}.";
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
    private void Apply(RosterMember member, IReadOnlyDictionary<GearSlot, uint> gear, int itemLevel)
    {
        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            if (!gear.TryGetValue(slot, out var itemId))
            {
                need.EquippedItemId = 0;
                need.EquippedSource = GearSource.None;
                continue;
            }

            need.EquippedItemId = itemId;
            need.EquippedSource = classifier.Classify(itemId);

            // Wearing it is proof of owning it, and catches everything picked up before the plugin
            // was ever opened. The reverse is not proof of anything, so nothing is turned off here.
            if (!need.IsWearingTarget)
                continue;

            if (need.Source == GearSource.Raid)
                need.Obtained = true;
            else if (need.Source == GearSource.TomeAugmented)
                need.UpgradeObtained = true;
        }

        member.LastScannedUtc = DateTime.UtcNow;

        if (itemLevel > 0)
            member.AverageItemLevel = itemLevel;

        config.Save();
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;

        if (phase != Phase.Idle)
            CloseExamine();
    }
}
