using System;
using Dalamud.Plugin.Services;
using LootMastr.Data;

namespace LootMastr.Roster;

/// <summary>
/// Keeps the roster's jobs matching what people are actually playing.
///
/// Only jobs, and only for people already in the roster — nobody is ever added, because a party
/// picks up strangers and the roster is a static. Swapping job mid-tier is meant to be an
/// emergency, but when it happens the role feeds the damage-dealer priority, and a stale job plans
/// for a role that is not there any more without anything looking wrong.
/// </summary>
public sealed class RosterSync : IDisposable
{
    /// <summary>Nobody changes job in under a few seconds, and this runs on the frame tick.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly RosterStore roster;
    private readonly PartyReader party;

    private DateTime lastChecked = DateTime.MinValue;

    public RosterSync(RosterStore roster, PartyReader party)
    {
        this.roster = roster;
        this.party = party;

        Services.Framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow - lastChecked < Interval)
            return;

        lastChecked = DateTime.UtcNow;

        if (!Services.ClientState.IsLoggedIn || Services.Party.Length == 0)
            return;

        var changed = roster.RefreshJobsFromParty(party.Read());
        if (changed > 0)
            Services.Log.Information($"Roster: refreshed {changed} job(s) from the party.");
    }

    public void Dispose() => Services.Framework.Update -= OnUpdate;
}
