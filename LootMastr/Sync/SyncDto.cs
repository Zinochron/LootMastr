using System;
using System.Collections.Generic;
using LootMastr.Data;
using LootMastr.Roster;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LootMastr.Sync;

/// <summary>
/// What a static looks like on the wire.
///
/// It is the static's own data, not a translation of it. That is a deliberate departure from how
/// <c>GearPlannerImport</c> is built: there the other side is XIVGear and can change under us, so a
/// separate shape is protection. Here both ends are this plugin and <b>the server never reads the
/// document</b> — it stores a blob and checks a token. A mapping layer between two shapes that are
/// identical by construction would be forty fields of ceremony that can only ever drift.
///
/// What actually protects against version skew is <see cref="SyncEnvelope.Schema"/> plus tolerant
/// reading: Newtonsoft drops fields a client does not know and defaults ones it is missing, so an
/// older plugin pulling a newer document loses the fields it could not have used anyway.
/// </summary>
public sealed class SyncDocument
{
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    [JsonProperty("roster")] public List<RosterMember> Roster { get; set; } = new();

    [JsonProperty("active_tier_id")] public string ActiveTierId { get; set; } = string.Empty;

    [JsonProperty("tier")] public TierDefinition? Tier { get; set; }

    [JsonProperty("kills")] public Dictionary<int, int> Kills { get; set; } = new();

    [JsonProperty("settings")] public StaticSettings Settings { get; set; } = new();

    /// <summary>
    /// Everything a group decides, and nothing about this machine.
    ///
    /// <see cref="StaticProfile.Sync"/> is pointedly absent: it holds this client's token, and a
    /// token uploaded to the thing it authenticates against would be a spectacular own goal.
    /// </summary>
    public static SyncDocument From(StaticProfile profile) => new()
    {
        Name = profile.Name,
        Roster = profile.Roster,
        ActiveTierId = profile.ActiveTierId,
        Tier = profile.Tier,
        Kills = profile.Kills,
        Settings = profile.Settings,
    };

    /// <summary>
    /// Writes a pulled document over a local static.
    ///
    /// Replaces rather than merges, which is what "last write wins" means on the way in as well as
    /// on the way out. The local id and the sync setup survive: one is this install's handle on the
    /// static and the other is its credentials, and neither is the group's business.
    /// </summary>
    public void ApplyTo(StaticProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(Name))
            profile.Name = Name;

        profile.Roster = Roster ?? new List<RosterMember>();
        profile.Kills = Kills ?? new Dictionary<int, int>();
        profile.Settings = Settings ?? new StaticSettings();

        if (!string.IsNullOrWhiteSpace(ActiveTierId))
            profile.ActiveTierId = ActiveTierId;

        // A document without a tier means the group has not set one up, not that ours should go.
        if (Tier != null)
            profile.Tier = Tier;
    }
}

/// <summary>
/// What the server wraps a document in, both directions.
///
/// Every field out here is one the server reads, so roles travel as <c>"read"</c>, <c>"write"</c> and
/// <c>"admin"</c> rather than as 0, 1 and 2. Inside <see cref="Document"/> the plugin's own
/// serialisation applies, numbered enums and all — that half is a blob the server never opens, and a
/// wire format nobody parses is not a wire format.
/// </summary>
public sealed class SyncEnvelope
{
    /// <summary>Shape of <see cref="Document"/>. Bumped only when a change is not backwards readable.</summary>
    [JsonProperty("schema")] public int Schema { get; set; } = 1;

    /// <summary>Server-side counter, one up per accepted write. Never reused.</summary>
    [JsonProperty("revision")] public long Revision { get; set; }

    [JsonProperty("updated_by")] public string UpdatedBy { get; set; } = string.Empty;

    [JsonProperty("updated_at")] public DateTime? UpdatedAt { get; set; }

    /// <summary>What this character may do, as the server sees it. Advisory: the server re-checks.</summary>
    [JsonProperty("role")]
    [JsonConverter(typeof(StringEnumConverter), true)]
    public StaticRole Role { get; set; } = StaticRole.Read;

    [JsonProperty("document")] public SyncDocument? Document { get; set; }

    /// <summary>
    /// Set on a write whose <c>base_revision</c> was behind. The write still happened — the group
    /// chose last-write-wins — and this is what turns a silent loss into a visible one.
    /// </summary>
    [JsonProperty("overwrote")] public long? Overwrote { get; set; }
}

/// <summary>A write, with the revision the client believed it was editing.</summary>
public sealed class SyncPush
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;

    [JsonProperty("base_revision")] public long BaseRevision { get; set; }

    [JsonProperty("document")] public SyncDocument Document { get; set; } = new();
}

/// <summary>Creating a static, or claiming a token on one that exists.</summary>
public sealed class SyncJoin
{
    [JsonProperty("password")] public string Password { get; set; } = string.Empty;

    [JsonProperty("character")] public string Character { get; set; } = string.Empty;
}

/// <summary>What the server hands back once a password has been accepted. The password is not kept.</summary>
public sealed class SyncClaim
{
    [JsonProperty("token")] public string Token { get; set; } = string.Empty;

    [JsonProperty("role")]
    [JsonConverter(typeof(StringEnumConverter), true)]
    public StaticRole Role { get; set; } = StaticRole.Read;

    [JsonProperty("character")] public string Character { get; set; } = string.Empty;
}

/// <summary>One character's standing with one static.</summary>
public sealed class SyncMember
{
    [JsonProperty("character")] public string Character { get; set; } = string.Empty;

    [JsonProperty("role")]
    [JsonConverter(typeof(StringEnumConverter), true)]
    public StaticRole Role { get; set; } = StaticRole.Read;

    [JsonProperty("claimed_at")] public DateTime? ClaimedAt { get; set; }
}

/// <summary>What the server says when it refuses. Always json, never a bare status code.</summary>
public sealed class SyncError
{
    [JsonProperty("error")] public string Error { get; set; } = string.Empty;

    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>One entry of the public tier library.</summary>
public sealed class TierListing
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    [JsonProperty("updated_at")] public DateTime? UpdatedAt { get; set; }
}
