using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkEventType = FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Automation;

/// <summary>
/// Hands one item to one player, through the three windows the game actually uses.
///
/// The flow, taken from a recorded Lootmaster chest rather than guessed:
///
/// <list type="number">
/// <item><c>NeedGreed</c> — the chest. Selecting a row and pressing Loot Recipient opens…</item>
/// <item><c>NeedGreedTargeting</c> — <c>[0]</c> the loot index, <c>[4]</c> the item name,
/// <c>[6]</c> how many candidates, <c>[7…]</c> their names. Picking one opens…</item>
/// <item><c>SelectYesno</c> — "Allow &lt;player&gt; to claim the &lt;item&gt;?"</item>
/// </list>
///
/// The candidate list is <b>not</b> in party order — the recording had the local player first and
/// the party list the other way round — so recipients are matched by name, never by index.
///
/// <b>Which item is being assigned is decided by the chest's selection, not by the button press.</b>
/// The Loot Recipient button carries no item: it acts on whatever row is selected. A recording of a
/// failed run made that plain — the plugin pressed the button for the ring and the game opened the
/// earring, because the earring was the last row the player had clicked and the plugin's attempt to
/// move the selection had done nothing at all. So the selection is now moved through the list's own
/// item event, and then <b>read back and checked</b> before the button is pressed. If it cannot be
/// moved, nothing is pressed.
///
/// Every step is verified against what the game put on screen afterwards, because a recording is one
/// client on one patch. Nothing is irreversible until the Yes on the last dialog, and that is only
/// pressed once the dialog's own text names both the intended player and the intended item.
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

    /// <summary>How often to try moving the chest's selection before giving up on it.</summary>
    private const int SelectionAttempts = 3;

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
    /// Every one of those parameters identifies the <i>control</i>, not the row: they stayed the
    /// same across three different rows and three different recipients. The row lives in the list's
    /// own state, which is why it is set there instead of being encoded in a parameter.
    /// </summary>
    /// <summary>
    /// The chest's first action, which is <b>Greed only</b>.
    ///
    /// Not a guess. Two earlier versions of the assignment flow tried callback shapes on this window
    /// on the reasoning that a wrong one does nothing, and <c>[0, index]</c> turned out to press this
    /// — settling an item for good with no dialog in between. That accident is the evidence, and it
    /// is why the number is written down here instead of being tried again.
    /// </summary>
    private const int GreedOnlyAction = 0;

    private const int LootRecipientButton = 5;

    private const int ConfirmButton = 0;

    private enum Phase
    {
        Idle,
        SelectRow,
        PressRecipient,
        OpenTargeting,
        PickRecipient,
        Confirm,
        Done,
    }

    private readonly Configuration config;
    private readonly SafetyGuard guard;

    private Phase phase = Phase.Idle;
    private LiveLootItem item;
    private string recipient = string.Empty;
    private DateTime stepStarted;
    private DateTime lastAction = DateTime.MinValue;
    private int attempt;

    public LootAssignmentRunner(Configuration config, SafetyGuard guard)
    {
        this.config = config;
        this.guard = guard;

        Services.Framework.Update += OnUpdate;
    }

    /// <summary>
    /// Puts one row up for greed, in a single press that nothing can take back.
    ///
    /// Every other thing this class does has a verification gate between the attempt and the
    /// consequence, which is what makes retrying safe. This one has none: the game shows no
    /// confirmation, and a wrong row settles somebody's coffer. So it is one press on a selection
    /// that has been read back first, and a failure is reported rather than worked around.
    ///
    /// Not part of the state machine either. There is nothing to wait for.
    /// </summary>
    public unsafe string Greed(LiveLootItem target)
    {
        if (IsRunning)
            return "Busy assigning something else.";

        var verdict = guard.CheckAssign();
        if (!verdict.Ok)
            return verdict.Reason;

        if (!SelectChestRow(target.Index))
            return "Could not reach that row in the chest.";

        // Read back before anything is pressed. The action works on whatever the chest has
        // selected, so an unverified selection is an unverified target.
        if (!SelectionIsOn(target.Index))
            return "The chest did not take the selection — nothing was pressed.";

        var addon = AddonReader.Find(ChestAddon);
        if (addon.IsNull)
            return "The chest window went away.";

        var unitBase = (AtkUnitBase*)addon.Address;
        if (unitBase == null)
            return "The chest window went away.";

        var values = stackalloc AtkValue[2];
        values[0].SetInt(GreedOnlyAction);
        values[1].SetInt(target.Index);

        unitBase->FireCallback(2, values, false);

        lastAction = DateTime.UtcNow;
        Status = $"{target.Name} put up for greed.";

        return Status;
    }

    public bool IsRunning => phase is not (Phase.Idle or Phase.Done);

    public string Status { get; private set; } = string.Empty;

    /// <summary>
    /// What the last run put in front of the game, and for whom.
    ///
    /// Deliberately not called "completed". An awarded coffer stays in the chest — the recording
    /// shows a slot still opening its assignment window after it had been handed over — so the
    /// window cannot say whether anything changed hands, and neither can this. The game can still
    /// refuse: a unique coffer the recipient already owns comes back with an error. What actually
    /// settles a row is the obtain line in chat, which <see cref="ObtainTracker"/> watches for.
    /// </summary>
    public LiveLootItem? Offered { get; private set; }

    public string OfferedRecipient { get; private set; } = string.Empty;

    /// <summary>Called by the caller once it has noted <see cref="Offered"/>.</summary>
    public void ClearOffered()
    {
        Offered = null;
        OfferedRecipient = string.Empty;
    }

    public string Start(LiveLootItem target, string playerName)
    {
        if (IsRunning)
            return "Already assigning something.";

        var verdict = guard.CheckAssign();
        if (!verdict.Ok)
            return verdict.Reason;

        item = target;
        recipient = playerName;
        attempt = 0;
        phase = Phase.SelectRow;
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
            case Phase.SelectRow:
                SelectRow();
                break;

            case Phase.PressRecipient:
                PressRecipient();
                break;

            case Phase.OpenTargeting:
                OpenTargeting();
                break;

            case Phase.PickRecipient:
                PickRecipient();
                break;

            case Phase.Confirm:
                Confirm();
                break;
        }
    }

    /// <summary>Puts the chest's selection on the item this run is about.</summary>
    private void SelectRow()
    {
        // Already open on the right item — the player got there first. Carry on from there.
        if (TargetingMatchesItem())
        {
            Advance(Phase.PickRecipient);
            return;
        }

        if (AddonReader.IsOpen(TargetingAddon))
        {
            // Someone else's item is open. Leave it alone rather than clicking through it.
            Fail("The assignment window is open for a different item — close it and try again.");
            return;
        }

        if (!SelectChestRow(item.Index))
        {
            Fail($"{item.Name}: the chest's item list could not be read, so nothing was pressed. " +
                 "Click the row in the game window yourself and press Assign again.");

            return;
        }

        Advance(Phase.PressRecipient);
    }

    /// <summary>
    /// Presses Loot Recipient — but only once the chest agrees about which row that means.
    ///
    /// This check is the whole point. The button has no idea which item it is for; it reads the
    /// selection. Pressing it on the wrong row does not fail, it opens the wrong item's assignment
    /// window, and that is how a coffer already handed over got offered again.
    /// </summary>
    private void PressRecipient()
    {
        if (TargetingMatchesItem())
        {
            Advance(Phase.PickRecipient);
            return;
        }

        if (AddonReader.IsOpen(TargetingAddon))
        {
            Fail("The assignment window is open for a different item — close it and try again.");
            return;
        }

        if (!SelectionIsOn(item.Index))
        {
            if (attempt >= SelectionAttempts)
            {
                Fail($"{item.Name}: the chest still has another row selected, so nothing was " +
                     "pressed. Click the row in the game window yourself, then press Assign again.");

                return;
            }

            attempt++;
            SelectChestRow(item.Index);
            return;
        }

        ClickButton(ChestAddon, LootRecipientButton);
        Advance(Phase.OpenTargeting);
    }

    private void OpenTargeting()
    {
        if (TargetingMatchesItem())
        {
            Advance(Phase.PickRecipient);
            return;
        }

        // Only reachable if the selection moved between the check and the press.
        if (AddonReader.IsOpen(TargetingAddon))
        {
            Fail($"The game opened the assignment window for something other than {item.Name}. " +
                 "Nothing was chosen — close it and try again.");
        }
    }

    private void PickRecipient()
    {
        if (AddonReader.IsOpen(ConfirmAddon))
        {
            Advance(Phase.Confirm);
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
            Finish();
            return;
        }

        // Yes is 0 on SelectYesno.
        ClickButton(ConfirmAddon, ConfirmButton);
        Status = $"{item.Name} → {recipient}, confirmed.";
        Finish();
    }

    /// <summary>
    /// Ends the run at the point the plugin's part is over. Whether the item really changed hands
    /// is not something this can see — the chest keeps showing an awarded coffer, and the game
    /// refuses a unique item the recipient already owns — so it is reported as offered and left for
    /// chat to settle.
    /// </summary>
    private void Finish()
    {
        Offered = item;
        OfferedRecipient = recipient;
        phase = Phase.Done;
    }

    private void Advance(Phase next)
    {
        attempt = 0;
        stepStarted = DateTime.UtcNow;
        phase = next;
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
    /// Whether the chest is pointing at the row this run is about.
    ///
    /// Two places hold that answer and either will do: the addon's own <c>SelectedItemIndex</c> and
    /// the loot agent's <c>SelectedSlotIndex</c>. Both were seen tracking a hand-clicked row in the
    /// recording, and requiring both to agree would fail a run over a field that happens to lag a
    /// frame.
    /// </summary>
    private static unsafe bool SelectionIsOn(int index)
    {
        var addon = AddonReader.Find(ChestAddon);

        if (!addon.IsNull)
        {
            var chest = (AddonNeedGreed*)addon.Address;
            if (chest != null && chest->SelectedItemIndex == index)
                return true;
        }

        var agent = AgentLoot.Instance();
        return agent != null && agent->SelectedSlotIndex == index;
    }

    /// <summary>
    /// Selects a row in the chest, the way a click does.
    ///
    /// <c>SelectItem</c> alone was not enough: it moves the list's own highlight without telling the
    /// addon, so the Loot Recipient button went on acting on the row the player had clicked. The
    /// event that does reach the addon is the list item click, and the game builds it — index and
    /// all — in <c>DispatchItemEvent</c>. Hand-made list events are what crashed the client, so this
    /// asks the game for one rather than assembling it.
    /// </summary>
    private static unsafe bool SelectChestRow(int index)
    {
        var addon = AddonReader.Find(ChestAddon);
        if (addon.IsNull)
            return false;

        var chest = (AddonNeedGreed*)addon.Address;
        if (chest == null)
            return false;

        var list = FindItemList(chest);
        if (list == null)
            return false;

        // The game indexes its own renderers with this; out of range would walk off the end.
        if (index < 0 || index >= list->GetItemCount())
            return false;

        list->SelectItem(index, false);
        list->DispatchItemEvent(index, AtkEventType.ListItemClick);

        return true;
    }

    /// <summary>
    /// The chest's item list, found by asking each node id in turn and taking the one holding as
    /// many rows as the chest has items. The first list in the window is only a fallback — picking
    /// it blindly is what the earlier version did, and a wrong list means a selection that never
    /// moves.
    /// </summary>
    private static unsafe AtkComponentList* FindItemList(AddonNeedGreed* chest)
    {
        const uint highestNodeId = 64;

        var unitBase = (AtkUnitBase*)chest;
        AtkComponentList* fallback = null;

        for (uint nodeId = 1; nodeId <= highestNodeId; nodeId++)
        {
            var list = unitBase->GetComponentListById(nodeId);
            if (list == null)
                continue;

            if (fallback == null)
                fallback = list;

            if (chest->NumItems > 0 && list->GetItemCount() == chest->NumItems)
                return list;
        }

        return fallback;
    }

    /// <summary>
    /// Presses a button in a window.
    ///
    /// The version of this that crashed the client passed a <b>null</b> <c>AtkEventData</c>, and
    /// used it for list clicks as well — where the game's handler reads that data to work out which
    /// row was hit. Lists no longer go through here at all, and what is left gets a real, zeroed
    /// event and event data with the window as listener, which is the shape a genuine press arrives
    /// in.
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
    /// Picks a row in the recipient list. That window holds one list and its selection is read at
    /// the moment its own confirm button is pressed, so the highlight is enough here — and the
    /// window's text names the chosen player before anything irreversible happens.
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

            if (index < 0 || index >= list->GetItemCount())
                return false;

            list->SelectItem(index, false);
            list->DispatchItemEvent(index, AtkEventType.ListItemClick);

            return true;
        }

        return false;
    }

    private void Fail(string reason)
    {
        Status = reason;
        phase = Phase.Done;
        Services.Log.Warning($"Loot assignment stopped: {reason}");
    }

    public void Dispose() => Services.Framework.Update -= OnUpdate;
}
