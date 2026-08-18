using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;
using LootMastr.Planning;

namespace LootMastr.Roster;

/// <summary>
/// The static's line-up, kept in the config so it survives without anyone being online. Party
/// members are matched onto it by name and world; a match refreshes the job, nothing else.
/// </summary>
public sealed class RosterStore
{
    private readonly Configuration config;
    private readonly JobCatalog jobs;

    public RosterStore(Configuration config, JobCatalog jobs)
    {
        this.config = config;
        this.jobs = jobs;
    }

    public List<RosterMember> Members => config.Roster;

    public RosterMember? Find(string name, string world)
    {
        var key = RosterKey.For(name, world);
        return config.Roster.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public RosterMember Add(string name, string world, uint jobId = 0)
    {
        var existing = Find(name, world);
        if (existing != null)
            return existing;

        var member = new RosterMember { Name = name.Trim(), World = world.Trim(), JobId = jobId };
        config.Roster.Add(member);
        config.Save();
        return member;
    }

    public void Remove(RosterMember member)
    {
        config.Roster.Remove(member);
        config.Save();
    }

    public void Move(RosterMember member, int delta)
    {
        var index = config.Roster.IndexOf(member);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= config.Roster.Count)
            return;

        config.Roster.RemoveAt(index);
        config.Roster.Insert(target, member);
        config.Save();
    }

    /// <summary>
    /// Folds the current party into the roster: unknown players are added, known ones have their
    /// job refreshed. Jobs are worth taking from the party because people swap, and the role drives
    /// the damage-dealer priority.
    /// </summary>
    public int SyncFromParty(IEnumerable<PartyPlayer> party)
    {
        var added = 0;
        var changed = false;

        foreach (var player in party)
        {
            if (string.IsNullOrWhiteSpace(player.Name))
                continue;

            var member = Find(player.Name, player.World);
            if (member == null)
            {
                member = new RosterMember { Name = player.Name, World = player.World, JobId = player.JobId };
                config.Roster.Add(member);
                added++;
                changed = true;
                continue;
            }

            if (player.JobId != 0 && member.JobId != player.JobId)
            {
                member.JobId = player.JobId;
                changed = true;
            }
        }

        if (changed)
            config.Save();

        return added;
    }

    /// <summary>
    /// Changes a member's job. Nothing reads the job directly — everything goes through the role
    /// it implies — so this is also how the damage-dealer priority moves.
    /// </summary>
    public void SetJob(RosterMember member, uint jobId)
    {
        if (member.JobId == jobId)
            return;

        member.JobId = jobId;
        config.Save();
    }

    public RaidRole RoleOf(RosterMember member) => jobs.RoleOf(member.JobId);

    /// <summary>
    /// Cheap fingerprint of everything the planner reads. Lets a cached forecast notice that a box
    /// was ticked somewhere else without recomputing every frame to find out.
    /// </summary>
    public int Signature()
    {
        var hash = new HashCode();

        // Which static this is, first. Everything cached off this fingerprint — the forecast, the
        // coming week, the basis comparison — then notices a switch for free, and nothing needs an
        // event to be wired up and eventually forgotten.
        hash.Add(config.CurrentStaticId);

        // The rules the planner runs on, which are read here and set two tabs away. This used to be
        // missing and the Recalculate button was the workaround; syncing turned that from a small
        // annoyance into a wrong answer, because a pull changes the rules with nobody pressing
        // anything.
        var settings = config.Settings;
        var rules = settings.Rules;

        hash.Add(rules.UseRoleOrder);
        hash.Add(rules.UsePlayerOrder);
        hash.Add(rules.Spread);
        hash.Add(rules.Basis);
        hash.Add(rules.AltsMayTakeSpareGear);

        foreach (var role in rules.RoleOrder)
            hash.Add(role);

        hash.Add(settings.LookaheadWeeks);
        hash.Add(settings.AltCharacters);

        // Anything a pull brought with it, including a tier nobody here edited. One counter stands
        // in for a whole document, which is the only cheap way to notice a tier changed.
        hash.Add(config.Current.Sync.Revision);

        hash.Add(config.Roster.Count);

        foreach (var member in config.Roster)
        {
            hash.Add(member.Key);
            hash.Add(member.JobId);
            hash.Add(member.ItemsReceived);

            // Walked in a fixed order rather than over the dictionaries. Their order is not
            // guaranteed, and NeedFor inserts on access, so hashing them directly would make the
            // fingerprint change on its own — recomputing the whole plan every frame for nothing.
            foreach (var slot in Slots.All)
            {
                if (!member.Needs.TryGetValue(slot, out var need))
                    continue;

                hash.Add(slot);
                hash.Add(need.Source);
                hash.Add(need.Obtained);
                hash.Add(need.UpgradeObtained);

                // The tomestone piece under an augment decides whether a material dropping tonight
                // is worth anything to this player, so it moves the plan and belongs in here.
                hash.Add(need.BaseObtained);
            }

            for (var encounter = 1; encounter <= PlayerPlan.MaxEncounters; encounter++)
                hash.Add(member.TokensFor(encounter));

            // The weapon stone costs 500 tomestones, which shifts this player's whole vendor
            // timeline — a change the plan reads and therefore has to notice.
            hash.Add(member.WeaponTokenObtained);
            hash.Add(member.WeaponAugmentObtained);
            hash.Add(member.IsAlt);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Everyone the plan is about.
    ///
    /// The same list as <see cref="Members"/> with second characters removed when the feature is
    /// switched off. Everything that decides or forecasts reads this; only the roster editor reads
    /// the raw list, because that is the one screen where a hidden player has to still be visible or
    /// they could never be brought back.
    /// </summary>
    public IReadOnlyList<RosterMember> Active =>
        config.AltCharacters ? config.Roster : config.Roster.Where(m => !m.IsAlt).ToList();

    /// <summary>
    /// The roster as the roster tab lists it.
    ///
    /// Deliberately <b>not</b> <see cref="Active"/>. That one answers "who is in the running for a
    /// drop" and is a rule; this answers "what do I want to look at" and is a preference. A second
    /// character switched off is still in the plan's sense hidden either way, but somebody who has
    /// alts switched on may perfectly well not want six extra rows while reading gear.
    /// </summary>
    public IReadOnlyList<RosterMember> Visible =>
        config.ShowAlts ? config.Roster : config.Roster.Where(m => !m.IsAlt).ToList();

    /// <summary>Roster order, but damage dealers first — the order suggestions are shown in.</summary>
    public IEnumerable<RosterMember> ByPriority() =>
        config.Roster.OrderBy(m => RoleOf(m) == RaidRole.Dps ? 0 : 1)
              .ThenBy(config.Roster.IndexOf);
}
