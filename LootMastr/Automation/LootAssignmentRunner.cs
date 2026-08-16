using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkEventType = FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType;
using LootMastr.Data;

namespace LootMastr.Automation;

/// <summary>
/// Hands one item to one player, through the three windows the game actually uses.
///
/// The flow, taken from a recorded Lootmaster chest rather than guessed:
///
/// <list type="number">
/// <item><c>NeedGreed</c> — the chest. Clicking an item opens…</item>
/// <item><c>NeedGreedTargeting</c> — <c>[0]</c> the loot index, <c>[4]</c> the item name,
/// <c>[6]</c> how many candidates, <c>[7…]</c> their names. Picking one opens…</item>
/// <item><c>SelectYesno</c> — "Allow &lt;player&gt; to claim the &lt;item&gt;?"</item>
/// </list>
///
/// The candidate list is <b>not</b> in party order — the recording had the local player first and
/// the party list the other way round — so recipients are matched by name, never by index.
///
/// The window events are recorded too, not inferred. Two earlier attempts guessed at a callback
/// number: one pressed <b>Greed only</b> and settled an item, the next did nothing at all. What
/// drives each step now is the event a real click sends, read off a recording of three assignments.
///
/// Every step is still verified against what the game put on screen afterwards, because a recording
/// is one client on one patch. Nothing is irreversible until the Yes on the last dialog, and that is
/// only pressed once the dialog's own text names both the intended player and the intended item.
/// Nothing is ever tried twice: a wrong press in the chest is a decision, not a no-op.
/// </summary>
public sealed class LootAssignmentRunner : IDisposable
{
    private const string ChestAddon = "NeedGreed";
    private const string TargetingAddon = "NeedGreedTargeting";
    private const string ConfirmAddon = "SelectYesno";

    private const int TargetingLootIndex = 0;
    private const int TargetingItemName = 4;
    private const int TargetingCandidateCount = 6;
    private const int TargetingFirstCandidate = 7;
    private const int ConfirmPrompt = 0;

    private const int StepTimeoutMs = 4000;

    /// <summary>
    /// The events a real click sends, taken off a recording of three assignments in a live chest:
    ///
    /// <code>
    /// NeedGreed            ListItemClick  param=0   select the row
    /// NeedGreed            ButtonClick    param=5   "Loot Recipient"
    /// NeedGreedTargeting   ListItemClick  param=0   pick the name
    /// NeedGreedTargeting   ButtonClick    param=0   confirm
    /// </code>
    ///
    /// The recipient's <c>param</c> stayed 0 across three different recipients, so it identifies
    /// the list rather than the row — the chosen row lives in the list component's own
    /// <c>SelectedItemIndex</c>, which is why that is set instead of encoded here.
    /// </summary>
    private const int LootRecipientButton = 5;

    private const int ConfirmButton = 0;

    private enum Phase
    {
        Idle,
        OpenTargeting,
        PickRecipient,
        Confirm,
        VerifyGone,
        Done,
    }

    private readonly Configuration config;
    private readonly LootWindowReader loot;
    private readonly SafetyGuard guard;

    private Phase phase = Phase.Idle;
    private LiveLootItem item;
    private string recipient = string.Empty;
    private DateTime stepStarted;
    private DateTime lastAction = DateTime.MinValue;
    private int attempt;

    public LootAssignmentRunner(Configuration config, LootWindowReader loot, SafetyGuard guard)
    {
        this.config = config;
        this.loot = loot;
        this.guard = guard;

        Services.Framework.Update += OnUpdate;
    }

    public bool IsRunning => phase is not (Phase.Idle or Phase.Done);

    public string Status { get; private set; } = string.Empty;

    /// <summary>True when the last run finished with the item actually gone from the chest.</summary>
    public bool LastSucceeded { get; private set; }

    /// <summary>What the last successful run handed over, for the caller to write down once.</summary>
    public LiveLootItem? Completed { get; private set; }

    public string CompletedRecipient { get; private set; } = string.Empty;

    /// <summary>Called by the caller once it has recorded <see cref="Completed"/>.</summary>
    public void ClearCompleted()
    {
        Completed = null;
        CompletedRecipient = string.Empty;
    }

    public string Start(LiveLootItem target, string playerName)
    {
        // Off unless switched on, because an earlier version of this crashed the client. The list
        // clicks that most likely caused it are gone, but "most likely" is not something to turn on
        // behind somebody's back.
        if (!config.EnableAssignment)
        {
            Status = $"{target.Name} → {playerName}. Assign it in game, or switch assigning on in " +
                     "Settings once you have somewhere safe to try it.";

            return Status;
        }

        if (IsRunning)
            return "Already assigning something.";

        var verdict = guard.CheckAssign();
        if (!verdict.Ok)
            return verdict.Reason;

        item = target;
        recipient = playerName;
        attempt = 0;
        LastSucceeded = false;
        phase = Phase.OpenTargeting;
        stepStarted = DateTime.UtcNow;
        Status = $"Assigning {target.Name} to {playerName}…";

        return Status;
    }

    public void Stop(string reason)
    {
        if (phase == Phase.Idle)
            return;

        phase = Phase.Idle;
        Status = reason;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!IsRunning)
            return;

        // Every step is spaced out; firing at a window mid-transition double-selects.
        if ((DateTime.UtcNow - lastAction).TotalMilliseconds < config.ActionDelayMs)
            return;

        if ((DateTime.UtcNow - stepStarted).TotalMilliseconds > StepTimeoutMs)
        {
            Fail($"{item.Name}: the game did not respond as expected — nothing was assigned.");
            return;
        }

        switch (phase)
        {
            case Phase.OpenTargeting:
                OpenTargeting();
                break;

            case Phase.PickRecipient:
                PickRecipient();
                break;

            case Phase.Confirm:
                Confirm();
                break;

            case Phase.VerifyGone:
                VerifyGone();
                break;
        }
    }

    private void OpenTargeting()
    {
        // Already showing the right item? Then the click landed.
        if (TargetingMatchesItem())
        {
            attempt = 0;
            stepStarted = DateTime.UtcNow;
            phase = Phase.PickRecipient;
            return;
        }

        if (AddonReader.IsOpen(TargetingAddon))
        {
            // Someone else's item is open. Leave it alone rather than clicking through it.
            Fail("The targeting window is open for a different item — close it and try again.");
            return;
        }

        // One attempt, never a list of shapes to try. Each item in a Lootmaster chest offers two
        // actions, and a wrong press here does not do nothing — it hits "Greed only", which
        // settles that item for good.
        if (attempt > 0)
        {
            Fail($"{item.Name}: the assignment window did not open. The Debug tab's recorder shows " +
                 "what the buttons actually send if the layout has changed.");
            return;
        }

        attempt++;

        // Select the row, then press Loot Recipient — what a click on that row does.
        SelectInList(ChestAddon, item.Index);
        ClickButton(ChestAddon, LootRecipientButton);
    }

    private void PickRecipient()
    {
        if (AddonReader.IsOpen(ConfirmAddon))
        {
            attempt = 0;
            stepStarted = DateTime.UtcNow;
            phase = Phase.Confirm;
            return;
        }

        if (!TargetingMatchesItem())
        {
            Fail($"{item.Name}: the assignment window closed before a recipient was chosen.");
            return;
        }

        var candidate = CandidateIndexOf(recipient);
        if (candidate == null)
        {
            Fail($"{recipient} is not on the list for {item.Name} — they may be ineligible this week.");
            return;
        }

        if (attempt > 0)
        {
            Fail($"{item.Name}: could not choose {recipient} in the assignment window.");
            return;
        }

        attempt++;

        if (!SelectInList(TargetingAddon, candidate.Value))
        {
            Fail($"{item.Name}: the recipient list could not be read — nothing was chosen.");
            return;
        }

        ClickButton(TargetingAddon, ConfirmButton);
    }

    /// <summary>
    /// The one irreversible step, and the only one the game spells out in words. In
    /// <see cref="AssignmentMode.Confirm"/> it is deliberately left for the human — the game is
    /// already asking a clear question, and answering it is a better confirmation than anything
    /// the plugin could put on screen.
    /// </summary>
    private void Confirm()
    {
        var prompt = AddonReader.Text(AddonReader.Values(ConfirmAddon), ConfirmPrompt);
        if (prompt == null)
        {
            Fail($"{item.Name}: the confirmation dialog could not be read.");
            return;
        }

        if (!PromptMatches(prompt))
        {
            Fail($"The game asked \"{prompt}\", which is not {recipient} and {item.Name}. " +
                 "Nothing was confirmed — answer it yourself.");
            return;
        }

        if (config.Mode != AssignmentMode.Automatic)
        {
            Status = $"Ready: \"{prompt}\" — confirm it in game.";
            phase = Phase.Done;
            return;
        }

        // Yes is 0 on SelectYesno.
        ClickButton(ConfirmAddon, ConfirmButton);
        stepStarted = DateTime.UtcNow;
        phase = Phase.VerifyGone;
    }

    private void VerifyGone()
    {
        if (AddonReader.IsOpen(ConfirmAddon))
            return;

        // The item leaving the chest is the only proof it was handed over. It can legitimately
        // fail here — a unique item the recipient already owns is refused by the game — and that
        // has to be reported rather than retried.
        if (loot.Read().Any(i => i.Index == item.Index && i.ItemId == item.ItemId))
            return;

        LastSucceeded = true;
        Completed = item;
        CompletedRecipient = recipient;
        Status = $"{item.Name} → {recipient}.";
        phase = Phase.Done;
    }

    private bool TargetingMatchesItem()
    {
        if (!AddonReader.IsOpen(TargetingAddon))
            return false;

        var values = AddonReader.Values(TargetingAddon);

        if (AddonReader.Int(values, TargetingLootIndex) != item.Index)
            return false;

        var name = AddonReader.Text(values, TargetingItemName);
        return name != null && string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where the recipient sits in the window's own list. Matched by name because that list is not
    /// in party order — in the recording the local player came first and the party list disagreed.
    /// </summary>
    private static int? CandidateIndexOf(string playerName)
    {
        var values = AddonReader.Values(TargetingAddon);
        var count = AddonReader.Int(values, TargetingCandidateCount) ?? 0;

        for (var i = 0; i < count; i++)
        {
            var name = AddonReader.Text(values, TargetingFirstCandidate + i);
            if (name != null && string.Equals(name, playerName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    /// <summary>
    /// Whether the game's question is the one that was asked for. The prompt reads "Allow &lt;player&gt;
    /// to claim the &lt;item&gt;?" and lower-cases the item, so both halves are compared loosely — but
    /// both must be there, since this is the last thing standing before an irreversible click.
    /// </summary>
    private bool PromptMatches(string prompt) =>
        prompt.Contains(recipient, StringComparison.OrdinalIgnoreCase) &&
        prompt.Contains(item.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends a window the same event a click on it would. The event is built with its listener and
    /// target pointing at the window, which is what a real one carries — a zeroed event invites the
    /// game's own handler to walk a null pointer, and that takes the client with it.
    /// </summary>
    /// <summary>
    /// Presses a button in a window.
    ///
    /// The version of this that crashed the client passed a <b>null</b> <c>AtkEventData</c>, and
    /// used it for list clicks as well — where the game's handler reads that data to work out which
    /// row was hit. Lists no longer go through here at all (see <see cref="SelectInList"/>), and
    /// what is left gets a real, zeroed event and event data with the window as listener, which is
    /// the shape a genuine press arrives in.
    /// </summary>
    private unsafe void ClickButton(string addonName, int eventParam)
    {
        // Set even if the call falls through, so a failed step still waits its turn.
        lastAction = DateTime.UtcNow;

        var addon = AddonReader.Find(addonName);
        if (addon.IsNull)
            return;

        var unitBase = (AtkUnitBase*)addon.Address;
        if (unitBase == null || unitBase->RootNode == null)
            return;

        var eventData = new AtkEventData();

        var atkEvent = new AtkEvent
        {
            Listener = (AtkEventListener*)unitBase,
            Target = (AtkEventTarget*)unitBase->RootNode,
            Node = unitBase->RootNode,
            Param = (uint)eventParam,
        };

        unitBase->ReceiveEvent(AtkEventType.ButtonClick, eventParam, &atkEvent, &eventData);
    }

    /// <summary>
    /// Picks a row in a window's list, through the game's own <c>SelectItem</c> rather than by
    /// synthesising the click. That method builds whatever the list needs internally, which is
    /// exactly the part that could not be reconstructed from the outside — and hand-made list
    /// events are what took the client down.
    ///
    /// The click event carries the list's id rather than the row, which is why the recording showed
    /// the same parameter for three different recipients: the row is this call's argument.
    ///
    /// The list is found by asking for each node id in turn rather than hardcoding one. A wrong
    /// guess is caught by the checks that follow — the targeting window has to be showing the right
    /// item, and the confirmation has to name the right player — so it costs a failed step rather
    /// than the wrong person.
    /// </summary>
    private unsafe bool SelectInList(string addonName, int index)
    {
        const uint highestNodeId = 64;

        lastAction = DateTime.UtcNow;

        var addon = AddonReader.Find(addonName);
        if (addon.IsNull)
            return false;

        var unitBase = (AtkUnitBase*)addon.Address;
        if (unitBase == null)
            return false;

        for (uint nodeId = 1; nodeId <= highestNodeId; nodeId++)
        {
            var list = unitBase->GetComponentListById(nodeId);
            if (list == null)
                continue;

            list->SelectItem(index, true);
            return true;
        }

        return false;
    }

    private void Fail(string reason)
    {
        Status = reason;
        phase = Phase.Done;
        LastSucceeded = false;
        Services.Log.Warning($"Loot assignment stopped: {reason}");
    }

    public void Dispose() => Services.Framework.Update -= OnUpdate;
}
