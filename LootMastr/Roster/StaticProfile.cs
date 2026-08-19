using System;
using System.Collections.Generic;
using LootMastr.Data;
using LootMastr.Planning;
using Newtonsoft.Json;

namespace LootMastr.Roster;

/// <summary>How far the plugin is allowed to go once it knows who should get a drop.</summary>
public enum AssignmentMode
{
    /// <summary>Rank the candidates and stop there. Nothing is ever clicked.</summary>
    SuggestOnly,

    /// <summary>Show the assignment and wait for a button press before touching the loot window.</summary>
    Confirm,

    /// <summary>Assign without asking.</summary>
    Automatic,
}

/// <summary>
/// What a group does with the mount. Both are real policies rather than one being the absence of
/// the other, which is why this is a pair of choices and not a checkbox.
/// </summary>
public enum MountHandling
{
    /// <summary>Hand it to somebody, in reverse of the gear order.</summary>
    Assign,

    /// <summary>Put it up for greed and let the dice decide.</summary>
    GreedOnly,
}

/// <summary>
/// Everything a group decides together.
///
/// The split between this and <see cref="Configuration"/> is the one question the whole sync design
/// turns on: what belongs to the static and what belongs to this client. The answer the group gave
/// is <b>everything except the debug tab</b> — the distribution rules obviously, but the automation
/// settings too, because a static that agrees on how loot is handed out has agreed on that as well.
///
/// One that looks alarming and is not: <see cref="AssignmentMode.Automatic"/> travelling to every
/// client. <c>SafetyGuard.CheckAssign</c> still demands party leader and the Lootmaster rule, so on
/// anybody who is not distributing the setting does exactly nothing.
/// </summary>
[Serializable]
public sealed class StaticSettings
{
    // ---- Distribution ----------------------------------------------------

    /// <summary>Weights the simulator judges an assignment by.</summary>
    public PriorityRules Rules { get; set; } = new();

    /// <summary>How many weeks the planner looks ahead when judging an assignment.</summary>
    public int LookaheadWeeks { get; set; } = 8;

    /// <summary>
    /// Drops the group has decided by hand, overruling the rules for exactly those.
    ///
    /// Shared, because it is a record of what the group agreed rather than of what one person
    /// prefers to look at. Off until somebody switches it on, and off means the plan is what it
    /// always was.
    /// </summary>
    public ManualPlan Manual { get; set; } = new();

    // ---- Automation ------------------------------------------------------

    public AssignmentMode Mode { get; set; } = AssignmentMode.Confirm;

    /// <summary>Announce each assignment in party chat. Off by default — it talks for you.</summary>
    public bool AnnounceInPartyChat { get; set; }

    /// <summary>What to do with the mount the last fight always drops.</summary>
    public MountHandling Mount { get; set; } = MountHandling.Assign;

    public bool AltCharacters { get; set; }

    public bool AltsPreferredForWeaponTokens { get; set; } = true;

    /// <summary>
    /// Spacing between two actions in the loot window. Firing every frame while a window
    /// transitions double-selects entries, so actions are deliberately slow.
    /// </summary>
    public int ActionDelayMs { get; set; } = 400;

    /// <summary>Echo what the plugin is doing into the local chat log.</summary>
    public bool VerboseChat { get; set; }

    // ---- Expert mode -----------------------------------------------------

    public bool ExpertMode { get; set; }

    public bool AutoReadGearOnEnter { get; set; } = true;
}

/// <summary>
/// How this client talks to the server about one static, and the one part of a static that is
/// <b>never</b> shared: it is about this machine and this character.
///
/// <see cref="Token"/> is stored and the password is not. The password only ever crosses the wire
/// once, to claim a token, and is discarded immediately after — so a stolen config file leaks
/// access to one static as one character, not the ability to become its admin.
/// </summary>
[Serializable]
public sealed class SyncSetup
{
    public bool Enabled { get; set; }

    /// <summary>Base URL of the API, shown in plain text wherever this static is configured.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The static's name on the server, which is not necessarily its local name.</summary>
    public string RemoteName { get; set; } = string.Empty;

    /// <summary>Which character this client claimed the token as.</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>Bearer token bound to that character. Empty means "not claimed yet".</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>What the server last said this character may do. Advisory — the server re-checks.</summary>
    public StaticRole Role { get; set; } = StaticRole.Read;

    /// <summary>Revision this client last pulled, echoed on write so an overwrite can be reported.</summary>
    public long Revision { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    // Derived, all three, and kept out of the file. They are read-only so nothing could load them
    // back in — but written next to a token they read like permissions, and a config file that
    // appears to grant admin is a config file somebody will eventually try to edit.
    [JsonIgnore]
    public bool IsClaimed => Enabled && !string.IsNullOrEmpty(Token);

    /// <summary>
    /// Whether this client may change the static.
    ///
    /// A static nobody is syncing is always writable — it is a file on one machine and there is
    /// nobody to take turns with. Only once a server is involved and it has said <c>read</c> does
    /// this go false, because only then is there somebody whose work would be overwritten.
    /// </summary>
    [JsonIgnore]
    public bool CanWrite => !IsClaimed || Role != StaticRole.Read;

    [JsonIgnore]
    public bool IsAdmin => IsClaimed && Role == StaticRole.Admin;
}

/// <summary>What one character may do with one static. Decided by the server, never by the client.</summary>
public enum StaticRole
{
    Read,
    Write,
    Admin,
}

/// <summary>
/// One group: its line-up, its tier, its rules.
///
/// A client can be in several — an alt's owner is in the same static twice over, and somebody who
/// raids in two groups keeps two of these — so nothing here may be assumed to be the only one.
/// </summary>
[Serializable]
public sealed class StaticProfile
{
    /// <summary>Local identity, stable across renames. Not the server's id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "My static";

    /// <summary>
    /// The static, in priority order. The order is a real setting: it is the last tiebreak when two
    /// players are otherwise equal candidates for a drop.
    /// </summary>
    public List<RosterMember> Roster { get; set; } = new();

    /// <summary>File name (without extension) of the tier definition this group is running.</summary>
    public string ActiveTierId { get; set; } = "aac-heavyweight-savage";

    /// <summary>
    /// The tier actually in use. Seeded from the shipped json the first time and edited from then
    /// on, so a correction made in game is not lost the next time the plugin updates.
    /// </summary>
    public TierDefinition? Tier { get; set; }

    /// <summary>
    /// How often the group has cleared each fight, by encounter index. Group level rather than per
    /// player: it is what says how many books <i>should</i> have gone out.
    /// </summary>
    public Dictionary<int, int> Kills { get; set; } = new();

    public StaticSettings Settings { get; set; } = new();

    public SyncSetup Sync { get; set; } = new();

    public int KillsFor(int encounter) => Kills.GetValueOrDefault(encounter);

    /// <summary>
    /// Folds a version 1 config into one static.
    ///
    /// Pure, and separated from the file handling around it for one reason: this is the function
    /// that either moves somebody's whole roster across or quietly drops half of it, and the only
    /// way to know which is to be able to run it without a game attached.
    ///
    /// Every field falls back to what a fresh static would have had, so a config missing a setting —
    /// an install that predates it — comes out with the default rather than with a zero.
    /// </summary>
    public static StaticProfile FromLegacy(LegacyConfigV1 old)
    {
        var profile = new StaticProfile
        {
            Name = "My static",
            Roster = old.Roster ?? new List<RosterMember>(),
            Tier = old.Tier,
            Kills = old.Kills ?? new Dictionary<int, int>(),
        };

        if (!string.IsNullOrWhiteSpace(old.ActiveTierId))
            profile.ActiveTierId = old.ActiveTierId!;

        var settings = profile.Settings;

        if (old.Rules != null) settings.Rules = old.Rules;
        if (old.LookaheadWeeks is { } weeks) settings.LookaheadWeeks = weeks;
        if (old.Mode is { } mode) settings.Mode = mode;
        if (old.AnnounceInPartyChat is { } announce) settings.AnnounceInPartyChat = announce;
        if (old.Mount is { } mount) settings.Mount = mount;
        if (old.AltCharacters is { } alts) settings.AltCharacters = alts;
        if (old.AltsPreferredForWeaponTokens is { } preferred) settings.AltsPreferredForWeaponTokens = preferred;
        if (old.ActionDelayMs is { } delay) settings.ActionDelayMs = delay;
        if (old.VerboseChat is { } verbose) settings.VerboseChat = verbose;
        if (old.ExpertMode is { } expert) settings.ExpertMode = expert;
        if (old.AutoReadGearOnEnter is { } autoRead) settings.AutoReadGearOnEnter = autoRead;

        return profile;
    }
}

/// <summary>
/// A version 1 config, as read off disk. Every field is nullable and means "the old file did not
/// say", which is not the same as "the old file said false".
/// </summary>
public sealed record LegacyConfigV1(
    List<RosterMember>? Roster,
    string? ActiveTierId,
    TierDefinition? Tier,
    Dictionary<int, int>? Kills,
    PriorityRules? Rules,
    int? LookaheadWeeks,
    bool? ShowOnlyNextRecipient,
    AssignmentMode? Mode,
    bool? AnnounceInPartyChat,
    MountHandling? Mount,
    bool? AltCharacters,
    bool? AltsPreferredForWeaponTokens,
    int? ActionDelayMs,
    bool? VerboseChat,
    bool? ExpertMode,
    bool? AutoReadGearOnEnter);
