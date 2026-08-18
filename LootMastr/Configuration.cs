using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Configuration;
using LootMastr.Data;
using LootMastr.Planning;
using LootMastr.Roster;
using Newtonsoft.Json;

namespace LootMastr;

/// <summary>
/// What this install knows: a list of statics, which one is open, and nothing else.
///
/// Everything that used to sit here flat — the roster, the tier, every setting — belongs to a
/// <see cref="StaticProfile"/> now. What is left behind are <b>forwarders</b>: <c>config.Rules</c>
/// still reads and writes, it just lands in the current static. That is deliberate and it is what
/// kept this change small: the roster store, the planner, the automation and six tabs never learned
/// that statics exist.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>2: statics. 1 was one implicit static stored flat.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public List<StaticProfile> Statics { get; set; } = new();

    /// <summary>Loaded when the plugin starts. Empty means "whichever was open last".</summary>
    public string PrimaryStaticId { get; set; } = string.Empty;

    public string CurrentStaticId { get; set; } = string.Empty;

    /// <summary>
    /// The static everything else means when it says "the roster" or "the tier".
    ///
    /// Creates one when there is none, because every other member of this class assumes it exists —
    /// a plugin with no static is a state nothing downstream is written to survive, and the empty
    /// roster message already covers the case where it has nobody in it.
    /// </summary>
    [JsonIgnore]
    public StaticProfile Current
    {
        get
        {
            if (Statics.Count == 0)
                Statics.Add(new StaticProfile());

            var found = Statics.FirstOrDefault(s => s.Id == CurrentStaticId) ??
                        Statics.FirstOrDefault(s => s.Id == PrimaryStaticId) ??
                        Statics[0];

            CurrentStaticId = found.Id;
            return found;
        }
    }

    // ---- Forwarders ---------------------------------------------------------------------------
    //
    // Every one of these is [JsonIgnore]: the data lives in the static and is written there once.
    // A forwarder that serialised would put a second copy at the top level, and the next load would
    // have two answers to the same question.

    [JsonIgnore] public List<RosterMember> Roster => Current.Roster;

    [JsonIgnore] public Dictionary<int, int> Kills => Current.Kills;

    [JsonIgnore] public StaticSettings Settings => Current.Settings;

    [JsonIgnore] public PriorityRules Rules => Current.Settings.Rules;

    [JsonIgnore]
    public string ActiveTierId
    {
        get => Current.ActiveTierId;
        set => Current.ActiveTierId = value;
    }

    [JsonIgnore]
    public TierDefinition? Tier
    {
        get => Current.Tier;
        set => Current.Tier = value;
    }

    [JsonIgnore]
    public int LookaheadWeeks
    {
        get => Current.Settings.LookaheadWeeks;
        set => Current.Settings.LookaheadWeeks = value;
    }

    [JsonIgnore]
    public bool ShowOnlyNextRecipient
    {
        get => Current.Settings.ShowOnlyNextRecipient;
        set => Current.Settings.ShowOnlyNextRecipient = value;
    }

    [JsonIgnore]
    public AssignmentMode Mode
    {
        get => Current.Settings.Mode;
        set => Current.Settings.Mode = value;
    }

    [JsonIgnore]
    public bool AnnounceInPartyChat
    {
        get => Current.Settings.AnnounceInPartyChat;
        set => Current.Settings.AnnounceInPartyChat = value;
    }

    [JsonIgnore]
    public MountHandling Mount
    {
        get => Current.Settings.Mount;
        set => Current.Settings.Mount = value;
    }

    [JsonIgnore]
    public bool AltCharacters
    {
        get => Current.Settings.AltCharacters;
        set => Current.Settings.AltCharacters = value;
    }

    [JsonIgnore]
    public bool AltsPreferredForWeaponTokens
    {
        get => Current.Settings.AltsPreferredForWeaponTokens;
        set => Current.Settings.AltsPreferredForWeaponTokens = value;
    }

    [JsonIgnore]
    public int ActionDelayMs
    {
        get => Current.Settings.ActionDelayMs;
        set => Current.Settings.ActionDelayMs = value;
    }

    [JsonIgnore]
    public bool VerboseChat
    {
        get => Current.Settings.VerboseChat;
        set => Current.Settings.VerboseChat = value;
    }

    [JsonIgnore]
    public bool ExpertMode
    {
        get => Current.Settings.ExpertMode;
        set => Current.Settings.ExpertMode = value;
    }

    [JsonIgnore]
    public bool AutoReadGearOnEnter
    {
        get => Current.Settings.AutoReadGearOnEnter;
        set => Current.Settings.AutoReadGearOnEnter = value;
    }

    public int KillsFor(int encounter) => Current.KillsFor(encounter);

    /// <summary>Whether this client may change the open static. True whenever nothing is syncing.</summary>
    [JsonIgnore] public bool CanWrite => Current.Sync.CanWrite;

    // ---- Version 1, read once and never written again -------------------------------------------
    //
    // Under the json names the old file used, so Newtonsoft still fills them in; the forwarders
    // above carry the C# names. Two members, one json name, and only one of them serialises.
    //
    // Kept rather than deleted because deleting them is what makes a migration unrepeatable: an
    // install that skips a version, or a config restored from a backup, still lands here.

    [JsonProperty("Roster")] public List<RosterMember>? LegacyRoster { get; set; }
    [JsonProperty("ActiveTierId")] public string? LegacyActiveTierId { get; set; }
    [JsonProperty("Tier")] public TierDefinition? LegacyTier { get; set; }
    [JsonProperty("Kills")] public Dictionary<int, int>? LegacyKills { get; set; }
    [JsonProperty("Rules")] public PriorityRules? LegacyRules { get; set; }
    [JsonProperty("LookaheadWeeks")] public int? LegacyLookaheadWeeks { get; set; }
    [JsonProperty("ShowOnlyNextRecipient")] public bool? LegacyShowOnlyNextRecipient { get; set; }
    [JsonProperty("Mode")] public AssignmentMode? LegacyMode { get; set; }
    [JsonProperty("AnnounceInPartyChat")] public bool? LegacyAnnounceInPartyChat { get; set; }
    [JsonProperty("Mount")] public MountHandling? LegacyMount { get; set; }
    [JsonProperty("AltCharacters")] public bool? LegacyAltCharacters { get; set; }
    [JsonProperty("AltsPreferredForWeaponTokens")] public bool? LegacyAltsPreferred { get; set; }
    [JsonProperty("ActionDelayMs")] public int? LegacyActionDelayMs { get; set; }
    [JsonProperty("VerboseChat")] public bool? LegacyVerboseChat { get; set; }
    [JsonProperty("ExpertMode")] public bool? LegacyExpertMode { get; set; }
    [JsonProperty("AutoReadGearOnEnter")] public bool? LegacyAutoReadGear { get; set; }

    /// <summary>
    /// Folds a version 1 config into a single static. Runs once, before anything reads a roster.
    ///
    /// The whole thing is written to be safe to run twice and safe to run on a config that never
    /// needed it: a non-empty <see cref="Statics"/> means somebody already did this, and every field
    /// falls back to the value a fresh static would have had.
    ///
    /// A copy of the file goes next to it first. This is the one change in the plugin's history that
    /// cannot be undone by editing a setting back, and a backup is one line.
    /// </summary>
    public bool Migrate()
    {
        if (Statics.Count > 0)
        {
            Version = CurrentVersion;
            return false;
        }

        // Nothing to fold and nothing to lose: a first run, not an upgrade.
        var upgrading = LegacyRoster is { Count: > 0 } || LegacyTier != null;

        if (upgrading)
            BackUpConfigFile();

        // The mapping itself is pure and lives in StaticProfile, where the harness can assert that
        // every field lands where it should. What is left here is file handling.
        var profile = StaticProfile.FromLegacy(new LegacyConfigV1(
                                                   LegacyRoster, LegacyActiveTierId, LegacyTier,
                                                   LegacyKills, LegacyRules, LegacyLookaheadWeeks,
                                                   LegacyShowOnlyNextRecipient, LegacyMode,
                                                   LegacyAnnounceInPartyChat, LegacyMount,
                                                   LegacyAltCharacters, LegacyAltsPreferred,
                                                   LegacyActionDelayMs, LegacyVerboseChat,
                                                   LegacyExpertMode, LegacyAutoReadGear));

        Statics.Add(profile);
        PrimaryStaticId = profile.Id;
        CurrentStaticId = profile.Id;

        ClearLegacy();
        Version = CurrentVersion;

        Services.Log.Information(
            $"Config migrated to v{CurrentVersion}: {profile.Roster.Count} roster member(s) moved into " +
            $"\"{profile.Name}\".");

        return upgrading;
    }

    /// <summary>
    /// The one line that makes the migration reversible. Best effort on purpose — a failure here
    /// must not stop the plugin loading, it just means the safety net is missing.
    /// </summary>
    private static void BackUpConfigFile()
    {
        try
        {
            var path = Services.PluginInterface.ConfigFile.FullName;
            if (!File.Exists(path))
                return;

            var backup = $"{path}.v1.bak";
            if (File.Exists(backup))
                return;

            File.Copy(path, backup);
            Services.Log.Information($"Kept a copy of the version 1 config at {backup}.");
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not back the config file up before migrating.");
        }
    }

    private void ClearLegacy()
    {
        LegacyRoster = null;
        LegacyActiveTierId = null;
        LegacyTier = null;
        LegacyKills = null;
        LegacyRules = null;
        LegacyLookaheadWeeks = null;
        LegacyShowOnlyNextRecipient = null;
        LegacyMode = null;
        LegacyAnnounceInPartyChat = null;
        LegacyMount = null;
        LegacyAltCharacters = null;
        LegacyAltsPreferred = null;
        LegacyActionDelayMs = null;
        LegacyVerboseChat = null;
        LegacyExpertMode = null;
        LegacyAutoReadGear = null;
    }

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}
