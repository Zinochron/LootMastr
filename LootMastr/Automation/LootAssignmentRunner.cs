using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
/// What is inferred rather than recorded is which callback drives each window, so every step is
/// attempted and then <b>verified against what the game put on screen</b>. Nothing is irreversible
/// until the Yes on that last dialog, and that is only pressed once the dialog's own text names
/// both the intended player and the intended item.
///
/// Where that verification does not exist, nothing is tried twice. An item in a Lootmaster chest
/// offers two actions, and getting the callback wrong does not do nothing — it presses "Greed
/// only", which settles that item for good. So the chest is given exactly one attempt with a known
/// action id, and failure is reported rather than worked around.
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
    /// Picking a recipient inside the targeting window may be tried in a few shapes, because the
    /// step after it is the game asking "Allow &lt;player&gt; to claim &lt;item&gt;?" — a wrong shape
    /// either does nothing or names the wrong person, and both are caught there before anything
    /// irreversible happens.
    ///
    /// The chest itself gets no such list. See <see cref="OpenTargeting"/>.
    /// </summary>
    private static readonly int[][] RecipientPayloads =
    [
        [0, -1],
        [-1],
        [1, -1],
    ];

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
            Services.Log.Information($"Loot assignment: opened the window with action id {config.AssignActionId}.");
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

        // Exactly one attempt, and never a list of shapes to try.
        //
        // Each item in a Lootmaster chest offers two actions, and the wrong callback here does not
        // do nothing — it presses "Greed only", which is a decision about that item that cannot be
        // taken back. Trying shapes until one works is fine where the game asks for confirmation
        // afterwards, and this is not one of those places.
        if (attempt > 0)
        {
            Fail($"{item.Name}: the assignment window did not open. If the game's layout has " +
                 $"changed, the action id is in Settings (currently {config.AssignActionId}); " +
                 "the Debug tab's recorder shows what each one does.");
            return;
        }

        attempt++;
        Fire(ChestAddon, [config.AssignActionId, -1], item.Index);
    }

    private void PickRecipient()
    {
        if (AddonReader.IsOpen(ConfirmAddon))
        {
            Note("choosing a recipient", RecipientPayloads);
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

        if (attempt >= RecipientPayloads.Length)
        {
            Fail($"{item.Name}: could not choose {recipient} in the assignment window.");
            return;
        }

        Fire(TargetingAddon, RecipientPayloads[attempt++], candidate.Value);
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
        Fire(ConfirmAddon, [0], 0);
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

    private unsafe void Fire(string addonName, IReadOnlyList<int> payload, int index)
    {
        // Set even if the call falls through: a failed attempt still has to wait its turn, or the
        // remaining payload shapes would all be tried inside a single frame.
        lastAction = DateTime.UtcNow;

        var addon = AddonReader.Find(addonName);
        if (addon.IsNull)
            return;

        var unitBase = (AtkUnitBase*)addon.Address;
        if (unitBase == null)
            return;

        var values = stackalloc AtkValue[payload.Count];

        for (var i = 0; i < payload.Count; i++)
        {
            values[i].Type = AtkValueType.Int;

            // -1 in a payload template stands for "the thing being selected".
            values[i].Int = payload[i] == -1 ? index : payload[i];
        }

        unitBase->FireCallback((uint)payload.Count, values, false);
    }

    /// <summary>
    /// Writes down which callback shape actually worked. The list of shapes is inferred, so the
    /// first successful run is what turns it into a known one — after which the others can go.
    /// </summary>
    private void Note(string step, IReadOnlyList<int[]> shapes)
    {
        if (attempt == 0 || attempt > shapes.Count)
            return;

        Services.Log.Information(
            $"Loot assignment: {step} worked with payload [{string.Join(", ", shapes[attempt - 1])}] " +
            $"(shape {attempt} of {shapes.Count}).");
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
