using System;
using System.Linq;
using Dalamud.Game.DutyState;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Automation;

/// <summary>
/// Counts everyone's books. Every player who clears a fight gets one that week, which is half of
/// how a slot actually gets filled, so a forecast that ignores them is wrong from week two on.
///
/// The clear is noticed automatically but never counted silently: which fight a zone is only known
/// once its chest has been seen, and a book counted twice quietly corrupts the plan.
/// </summary>
public sealed class ClearTracker : IDisposable
{
    private readonly Configuration config;
    private readonly TierCatalog tiers;
    private readonly RosterStore roster;
    private readonly PartyReader party;

    public ClearTracker(Configuration config, TierCatalog tiers, RosterStore roster, PartyReader party)
    {
        this.config = config;
        this.tiers = tiers;
        this.roster = roster;
        this.party = party;

        Services.DutyState.DutyCompleted += OnDutyCompleted;
    }

    /// <summary>Fight that was just cleared and is waiting to be counted, if any.</summary>
    public TierEncounter? Pending { get; private set; }

    /// <summary>Names the books would go to, resolved when the clear happened.</summary>
    public string[] PendingPlayers { get; private set; } = [];

    public string Status { get; private set; } = string.Empty;

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        var territory = args.TerritoryType.RowId;
        var encounter = tiers.EncounterInTerritory(territory);
        if (encounter == null)
        {
            Status = $"Cleared zone {territory}, which is not tied to a fight yet — open the loot " +
                     "window once there and it will be.";
            return;
        }

        // Captured now rather than on confirm: people leave the instance immediately.
        PendingPlayers = party.Read()
                              .Select(p => roster.Find(p.Name, p.World)?.Key)
                              .Where(key => key != null)
                              .Select(key => key!)
                              .ToArray();

        Pending = encounter;
        Status = $"{encounter.Name} cleared.";
    }

    public void Confirm()
    {
        if (Pending == null)
            return;

        var counted = 0;

        foreach (var key in PendingPlayers)
        {
            var member = roster.Members.FirstOrDefault(m => m.Key == key);
            if (member == null)
                continue;

            member.Tokens[Pending.Index] = member.TokensFor(Pending.Index) + 1;
            counted++;
        }

        Status = $"{Pending.Name}: a book each for {counted} player(s).";
        Pending = null;
        PendingPlayers = [];
        config.Save();
    }

    public void Dismiss()
    {
        Pending = null;
        PendingPlayers = [];
        Status = string.Empty;
    }

    public void Dispose() => Services.DutyState.DutyCompleted -= OnDutyCompleted;
}
