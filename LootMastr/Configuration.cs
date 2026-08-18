using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Roster;

namespace LootMastr;

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

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // ---- The static ------------------------------------------------------

    /// <summary>
    /// The static, in priority order. The order is a real setting: it is the last tiebreak when two
    /// players are otherwise equal candidates for a drop.
    /// </summary>
    public List<RosterMember> Roster { get; set; } = new();

    // ---- Tier ------------------------------------------------------------

    /// <summary>
    /// File name (without extension) of the tier definition under <c>Data/Tiers</c>. Derived from
    /// the tier's name rather than typed, so it always matches the file it came from.
    /// </summary>
    public string ActiveTierId { get; set; } = "aac-heavyweight-savage";

    /// <summary>
    /// The tier actually in use. Seeded from the shipped json the first time and edited from then
    /// on, so a correction made in game is not lost the next time the plugin updates.
    /// </summary>
    public TierDefinition? Tier { get; set; }

    // ---- Distribution ----------------------------------------------------

    /// <summary>
    /// How often the group has cleared each fight, by encounter index. Group level rather than per
    /// player: it is what says how many books *should* have gone out, which is the number to check
    /// an individual's count against when it looks wrong.
    /// </summary>
    public Dictionary<int, int> Kills { get; set; } = new();

    public int KillsFor(int encounter) => Kills.GetValueOrDefault(encounter);

    /// <summary>Show only who each expected drop goes to, without the runners-up.</summary>
    public bool ShowOnlyNextRecipient { get; set; }

    /// <summary>How many weeks the planner looks ahead when judging an assignment.</summary>
    public int LookaheadWeeks { get; set; } = 8;

    /// <summary>Weights the simulator judges an assignment by.</summary>
    public PriorityRules Rules { get; set; } = new();

    // ---- Automation ------------------------------------------------------

    public AssignmentMode Mode { get; set; } = AssignmentMode.Confirm;

    /// <summary>Announce each assignment in party chat. Off by default — it talks for you.</summary>
    public bool AnnounceInPartyChat { get; set; } = false;

    /// <summary>What to do with the mount the last fight always drops.</summary>
    public MountHandling Mount { get; set; } = MountHandling.Assign;

    /// <summary>
    /// Whether the roster has second characters in it at all.
    ///
    /// Off hides them completely — out of the plan, the forecast, the ranking and every selector, as
    /// if they were not in the roster. They stay in the roster list itself, because a switch that
    /// deleted people would be a bad switch to have flicked by accident.
    /// </summary>
    public bool AltCharacters { get; set; }

    /// <summary>
    /// Whether an alt is the first place to look for the weapon stone and its material.
    ///
    /// The one thing an alt is genuinely a good home for. A tomestone weapon on a second character
    /// costs the raid nothing and makes the next clear go faster, which is the entire point of
    /// having one.
    /// </summary>
    public bool AltsPreferredForWeaponTokens { get; set; } = true;

    /// <summary>
    /// Spacing between two actions in the loot window. Firing every frame while a window
    /// transitions double-selects entries, so actions are deliberately slow.
    /// </summary>
    public int ActionDelayMs { get; set; } = 400;

    /// <summary>Echo what the plugin is doing into the local chat log.</summary>
    public bool VerboseChat { get; set; } = false;

    // ---- Expert mode ----------------------------------------------------------------------------

    /// <summary>
    /// Whether to work from exact gear rather than from what each slot is owed.
    ///
    /// Off, a slot is a word and a tick: "Raid, done". That is enough to run a distribution and it
    /// is what a static can keep up by hand. On, every slot carries the item actually equipped and
    /// the item aimed at, which is what a damage estimate needs — and which is only maintainable
    /// because the gear scan fills it in on its own.
    /// </summary>
    public bool ExpertMode { get; set; }

    /// <summary>
    /// Whether to read everyone's gear on its own after entering a duty.
    ///
    /// Expert mode lives or dies on the equipped side being current, and nobody presses a button
    /// eight times a week. Only roster members on a job of the role the roster expects are read;
    /// anyone else is skipped and named.
    /// </summary>
    public bool AutoReadGearOnEnter { get; set; } = true;

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}
