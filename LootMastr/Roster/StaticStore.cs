using System;
using System.Collections.Generic;
using System.Linq;

namespace LootMastr.Roster;

/// <summary>
/// Which static is open, and the four things that change that.
///
/// Reading the current static goes through <see cref="Configuration.Current"/> and does not need
/// this class at all — that is why almost nothing in the plugin depends on it. What needs a store is
/// the handful of operations that are more than a property: switching, creating, deleting, and
/// deciding which one opens on start.
/// </summary>
public sealed class StaticStore
{
    private readonly Configuration config;

    public StaticStore(Configuration config) => this.config = config;

    public StaticProfile Current => config.Current;

    public IReadOnlyList<StaticProfile> All => config.Statics;

    /// <summary>The static that opens with the plugin, or null when the last one used opens instead.</summary>
    public StaticProfile? Primary =>
        config.Statics.FirstOrDefault(s => s.Id == config.PrimaryStaticId);

    public bool IsCurrent(StaticProfile profile) => profile.Id == config.CurrentStaticId;

    public StaticProfile? Find(string id) => config.Statics.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Opens a different static.
    ///
    /// Nothing has to be invalidated by hand. Every cached forecast hangs off
    /// <c>RosterStore.Signature()</c>, which counts the static's id, and the tier catalogue resolves
    /// per static — so a switch is noticed by the same machinery that notices a ticked box.
    /// </summary>
    public bool Switch(string id)
    {
        var profile = Find(id);
        if (profile == null || profile.Id == config.CurrentStaticId)
            return false;

        config.CurrentStaticId = profile.Id;
        config.Save();
        return true;
    }

    public StaticProfile Create(string name)
    {
        var profile = new StaticProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New static" : name.Trim(),
        };

        config.Statics.Add(profile);
        config.CurrentStaticId = profile.Id;

        // The first one is the one that opens, without anybody having to say so.
        if (string.IsNullOrEmpty(config.PrimaryStaticId))
            config.PrimaryStaticId = profile.Id;

        config.Save();
        return profile;
    }

    /// <summary>
    /// Removes a static and everything in it. Never the last one: a plugin with no static is a state
    /// nothing downstream is written to survive, and "delete then create" is the same two clicks.
    /// </summary>
    public bool Delete(StaticProfile profile)
    {
        if (config.Statics.Count <= 1 || !config.Statics.Remove(profile))
            return false;

        if (config.PrimaryStaticId == profile.Id)
            config.PrimaryStaticId = string.Empty;

        if (config.CurrentStaticId == profile.Id)
            config.CurrentStaticId = config.Statics[0].Id;

        config.Save();
        return true;
    }

    /// <summary>Marks the static that opens on start, or clears the mark when it is already set.</summary>
    public void SetPrimary(StaticProfile? profile)
    {
        config.PrimaryStaticId = profile == null || config.PrimaryStaticId == profile.Id
                                     ? string.Empty
                                     : profile.Id;

        config.Save();
    }

    public void Rename(StaticProfile profile, string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == profile.Name)
            return;

        profile.Name = trimmed;
        config.Save();
    }

    /// <summary>
    /// Opens whichever static should be open when the plugin starts. Called once, from the plugin's
    /// constructor, because <see cref="Configuration.Current"/> otherwise reopens whatever was last
    /// used and a primary that is never honoured is not a setting.
    /// </summary>
    public void OpenPrimary()
    {
        if (Primary is { } primary)
            config.CurrentStaticId = primary.Id;
    }
}
