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

    /// <summary>
    /// Spacing between two actions in the loot window. Firing every frame while a window
    /// transitions double-selects entries, so actions are deliberately slow.
    /// </summary>
    public int ActionDelayMs { get; set; } = 400;

    /// <summary>Echo what the plugin is doing into the local chat log.</summary>
    public bool VerboseChat { get; set; } = false;

    /// <summary>
    /// Whether the plugin may drive the loot window itself.
    ///
    /// Off by default, and deliberately not remembered as "fine now": an earlier version of this
    /// crashed the game. The list clicks that most likely caused it have been replaced by the
    /// game's own <c>SelectItem</c>, but the first run after switching this on belongs in a duty
    /// nobody minds losing.
    /// </summary>
    public bool EnableAssignment { get; set; } = false;

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}
