using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using LootMastr.Data;
using LootMastr.Roster;
using Newtonsoft.Json;

namespace LootMastr.Sync;

/// <summary>Where syncing has got to, for the one control that shows it.</summary>
public enum SyncStatus
{
    /// <summary>Not switched on for this static. Grey, and a setting rather than a failure.</summary>
    Off,

    /// <summary>Switched on but this client has never claimed a token.</summary>
    NotJoined,

    Idle,
    Working,
    Error,
}

/// <summary>
/// Talks to the static server. One request in flight, applied on the framework thread.
///
/// The shape is <c>BisImporter</c>'s and for the same reason: a <see cref="Task"/> does the waiting
/// and <see cref="Poll"/>, called from the draw loop, does the applying. <b>Nothing here writes to a
/// roster off the framework thread.</b> Every field this touches is read by ImGui sixty times a
/// second, and a half-applied pull is a crash nobody can reproduce.
///
/// Everything is about the <i>current</i> static. Only one is open at a time and syncing is
/// per-static, so a queue of concurrent operations would be machinery for a situation that cannot
/// arise.
/// </summary>
public sealed class SyncClient : IDisposable
{
    /// <summary>Longest a pull may be stale before the next one is allowed.</summary>
    private static readonly TimeSpan PullEvery = TimeSpan.FromSeconds(60);

    /// <summary>How long a change has to sit still before it is pushed.</summary>
    private static readonly TimeSpan PushAfter = TimeSpan.FromSeconds(2);

    /// <summary>How often the document is hashed to notice a change. Not every frame.</summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private readonly Configuration config;
    private readonly StaticStore statics;
    private readonly TierCatalog tiers;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private Task<SyncOutcome>? running;
    private string runningFor = string.Empty;

    private DateTime lastPull = DateTime.MinValue;
    private DateTime lastCheck = DateTime.MinValue;
    private DateTime? changedAt;
    private string lastPushedHash = string.Empty;

    public SyncClient(Configuration config, StaticStore statics, TierCatalog tiers)
    {
        this.config = config;
        this.statics = statics;
        this.tiers = tiers;
    }

    public bool IsBusy => running is { IsCompleted: false };

    public string Message { get; private set; } = string.Empty;

    /// <summary>Members and their roles, from the last time they were asked for. Empty until then.</summary>
    public IReadOnlyList<SyncMember> Members { get; private set; } = [];

    public SyncStatus StatusOf(StaticProfile profile)
    {
        if (!profile.Sync.Enabled)
            return SyncStatus.Off;

        if (IsBusy && runningFor == profile.Id)
            return SyncStatus.Working;

        if (!profile.Sync.IsClaimed)
            return SyncStatus.NotJoined;

        return failed ? SyncStatus.Error : SyncStatus.Idle;
    }

    private bool failed;

    // ---- Operations -----------------------------------------------------------------------------

    /// <summary>Creates the static on the server and claims the first token, which is an admin's.</summary>
    public void Create(StaticProfile profile, string url, string remoteName, string password, string character)
    {
        if (!Begin(profile, url, out var problem))
        {
            Fail(problem);
            return;
        }

        var body = new SyncJoin { Password = password, Character = character };

        Start(profile, $"Creating {remoteName}…", async () =>
        {
            var claim = await PostAsync<SyncClaim>(url, "statics", token: null,
                                                   new { name = remoteName, password, character });

            return SyncOutcome.Claimed(claim, remoteName, url);
        });
    }

    /// <summary>Claims a token on a static that already exists. The password is not kept afterwards.</summary>
    public void Join(StaticProfile profile, string url, string remoteName, string password, string character)
    {
        if (!Begin(profile, url, out var problem))
        {
            Fail(problem);
            return;
        }

        Start(profile, $"Joining {remoteName}…", async () =>
        {
            var claim = await PostAsync<SyncClaim>(url, $"statics/{Uri.EscapeDataString(remoteName)}/join",
                                                   token: null,
                                                   new SyncJoin { Password = password, Character = character });

            return SyncOutcome.Claimed(claim, remoteName, url);
        });
    }

    public void Pull(StaticProfile profile, bool manual = false)
    {
        if (!profile.Sync.IsClaimed || IsBusy)
            return;

        var sync = profile.Sync;

        Start(profile, manual ? "Pulling…" : string.Empty, async () =>
        {
            var envelope = await GetAsync<SyncEnvelope>(sync.Url,
                                                        $"statics/{Uri.EscapeDataString(sync.RemoteName)}",
                                                        sync.Token);

            return SyncOutcome.Pulled(envelope);
        });
    }

    public void Push(StaticProfile profile, bool manual = false)
    {
        if (!profile.Sync.IsClaimed || IsBusy)
            return;

        var sync = profile.Sync;

        if (sync.Role == StaticRole.Read)
        {
            if (manual)
                Fail("This character may only read this static.");

            return;
        }

        var push = new SyncPush
        {
            BaseRevision = sync.Revision,
            Document = SyncDocument.From(profile),
        };

        var hash = HashOf(push.Document);

        Start(profile, manual ? "Pushing…" : string.Empty, async () =>
        {
            var envelope = await PutAsync<SyncEnvelope>(sync.Url,
                                                        $"statics/{Uri.EscapeDataString(sync.RemoteName)}",
                                                        sync.Token, push);

            return SyncOutcome.Pushed(envelope, hash);
        });
    }

    public void LoadMembers(StaticProfile profile)
    {
        if (!profile.Sync.IsClaimed || IsBusy)
            return;

        var sync = profile.Sync;

        Start(profile, "Reading members…", async () =>
        {
            var members = await GetAsync<List<SyncMember>>(
                              sync.Url, $"statics/{Uri.EscapeDataString(sync.RemoteName)}/members", sync.Token);

            return SyncOutcome.GotMembers(members);
        });
    }

    public void SetRole(StaticProfile profile, string character, StaticRole role)
    {
        if (!profile.Sync.IsClaimed || IsBusy)
            return;

        var sync = profile.Sync;

        Start(profile, $"Setting {character} to {role}…", async () =>
        {
            var members = await PutAsync<List<SyncMember>>(
                              sync.Url,
                              $"statics/{Uri.EscapeDataString(sync.RemoteName)}/members/{Uri.EscapeDataString(character)}",
                              sync.Token, new { role = role.ToString().ToLowerInvariant() });

            return SyncOutcome.GotMembers(members);
        });
    }

    // ---- The tier library -----------------------------------------------------------------------
    //
    // No token to read: a tier definition is item ids and prices, identical for everybody running
    // that raid, and it names nobody. Publishing takes one, from any static, so that a stranger
    // cannot overwrite what a group spent an evening correcting.

    /// <summary>What the library last said it had. Empty until somebody asks.</summary>
    public IReadOnlyList<TierListing> Library { get; private set; } = [];

    public void ListTiers(string url)
    {
        if (!Reachable(url, out var problem))
        {
            Fail(problem);
            return;
        }

        if (IsBusy)
            return;

        Start(statics.Current, "Reading the library…", async () =>
        {
            var listings = await GetAsync<List<TierListing>>(url, "tiers", token: null);
            return SyncOutcome.GotLibrary(listings);
        });
    }

    public void FetchTier(string url, string id)
    {
        if (!Reachable(url, out var problem))
        {
            Fail(problem);
            return;
        }

        if (IsBusy)
            return;

        Start(statics.Current, $"Fetching {id}…", async () =>
        {
            var definition = await GetAsync<TierDefinition>(url, $"tiers/{Uri.EscapeDataString(id)}",
                                                            token: null);

            return SyncOutcome.GotTier(definition, id);
        });
    }

    public void PublishTier(string url, string id, TierDefinition tier, string token)
    {
        if (!Reachable(url, out var problem))
        {
            Fail(problem);
            return;
        }

        if (IsBusy)
            return;

        if (string.IsNullOrEmpty(token))
        {
            Fail("Publishing needs a token. Join a static on a server first.");
            return;
        }

        Start(statics.Current, $"Publishing {tier.Name}…", async () =>
        {
            var listings = await PutAsync<List<TierListing>>(url, $"tiers/{Uri.EscapeDataString(id)}",
                                                             token, tier);

            return SyncOutcome.GotLibrary(listings);
        });
    }

    // ---- The loop -------------------------------------------------------------------------------

    /// <summary>
    /// Called from the draw loop. Applies whatever finished, then decides whether to start anything.
    ///
    /// Cheap when nothing is happening, which is almost always: a completed task is a reference
    /// compare, and the change check runs twice a second at most.
    /// </summary>
    public void Poll()
    {
        Apply();

        var profile = statics.Current;
        if (!profile.Sync.Enabled || !profile.Sync.IsClaimed || IsBusy)
            return;

        // A reader's local copy drifts as the obtain tracker and the gear scan write to it, and none
        // of that can go anywhere. Pulling still matters; watching for changes to push does not.
        var mayWrite = profile.Sync.Role != StaticRole.Read;

        var now = DateTime.UtcNow;

        // Somebody else's write is only visible by asking, so this is a poll and not a subscription.
        // Sixty seconds is a compromise nobody will notice either way: fast enough that a roster
        // edited before a pull is current by the time anyone reads it, slow enough to be free.
        if (now - lastPull > PullEvery)
        {
            lastPull = now;
            Pull(profile);
            return;
        }

        if (!mayWrite || now - lastCheck < CheckEvery)
            return;

        lastCheck = now;

        // Hashing the document is how a change is noticed at all. The roster's own fingerprint does
        // not cover the tier or the settings, and both of those are things a write-holder edits.
        var hash = HashOf(SyncDocument.From(profile));

        if (hash != lastPushedHash)
        {
            changedAt ??= now;

            // Debounced, because a slider being dragged is one change and not sixty.
            if (now - changedAt > PushAfter)
            {
                changedAt = null;
                Push(profile);
            }

            return;
        }

        changedAt = null;
    }

    /// <summary>Pulls now, whatever the timer says. For opening a window or switching static.</summary>
    public void Refresh()
    {
        lastPull = DateTime.MinValue;
        lastPushedHash = string.Empty;
        Members = [];
    }

    private void Apply()
    {
        if (running is not { IsCompleted: true })
            return;

        var task = running;
        var forId = runningFor;
        running = null;
        runningFor = string.Empty;

        var profile = statics.Find(forId);

        if (task.IsFaulted)
        {
            Fail(Explain(task.Exception?.GetBaseException()));
            return;
        }

        // The static went away while the request was in flight. Nothing to write it onto, and
        // nothing wrong either.
        if (profile == null)
        {
            Message = string.Empty;
            return;
        }

        var outcome = task.Result;
        failed = false;

        switch (outcome.Kind)
        {
            case SyncOutcomeKind.Claimed:
                profile.Sync.Enabled = true;
                profile.Sync.Url = outcome.Url;
                profile.Sync.RemoteName = outcome.RemoteName;
                profile.Sync.Token = outcome.Claim!.Token;
                profile.Sync.CharacterName = outcome.Claim.Character;
                profile.Sync.Role = outcome.Claim.Role;
                profile.Sync.Revision = 0;
                config.Save();

                Message = $"Joined as {outcome.Claim.Character} ({outcome.Claim.Role.ToString().ToLowerInvariant()}).";
                Refresh();
                break;

            case SyncOutcomeKind.Pulled:
                ApplyPull(profile, outcome.Envelope!);
                break;

            case SyncOutcomeKind.Pushed:
                profile.Sync.Revision = outcome.Envelope!.Revision;
                profile.Sync.LastSyncUtc = DateTime.UtcNow;
                lastPushedHash = outcome.Hash;
                config.Save();

                Message = outcome.Envelope.Overwrote is { } lost
                              ? $"Saved as revision {outcome.Envelope.Revision} — this overwrote revision {lost}, " +
                                "which somebody else had written since you last pulled."
                              : $"Saved as revision {outcome.Envelope.Revision}.";
                break;

            case SyncOutcomeKind.Members:
                Members = outcome.Members ?? [];
                Message = $"{Members.Count} character(s) know this static.";
                break;

            case SyncOutcomeKind.Library:
                Library = outcome.Listings ?? [];
                Message = Library.Count == 0
                              ? "The library has nothing in it yet."
                              : $"{Library.Count} tier(s) in the library.";
                break;

            case SyncOutcomeKind.Tier:
                tiers.Install(outcome.RemoteName, outcome.Tier!);
                tiers.Resolve();
                Message = $"Loaded \"{outcome.Tier!.Name}\" from the library.";
                break;
        }
    }

    private void ApplyPull(StaticProfile profile, SyncEnvelope envelope)
    {
        profile.Sync.Role = envelope.Role;
        profile.Sync.Revision = envelope.Revision;
        profile.Sync.LastSyncUtc = DateTime.UtcNow;

        if (envelope.Document is { } document)
        {
            document.ApplyTo(profile);

            // The tier arrived as data and has never been matched against this client's item sheet.
            tiers.Resolve();

            // What was pulled is by definition what the server has, so it is not a change to push
            // back — without this every pull would start a push and the two would chase each other.
            lastPushedHash = HashOf(SyncDocument.From(profile));
            changedAt = null;
        }

        config.Save();

        Message = $"Up to date at revision {envelope.Revision}" +
                  (string.IsNullOrEmpty(envelope.UpdatedBy) ? "." : $", last written by {envelope.UpdatedBy}.");
    }

    // ---- Plumbing -------------------------------------------------------------------------------

    /// <summary>
    /// Refuses to send a password anywhere it could be read off the wire.
    ///
    /// Loopback is allowed because that is where somebody developing the server will point it, and
    /// there is no wire. Everything else has to be https — this is the one request in the plugin
    /// that carries a secret somebody chose, and people reuse passwords.
    /// </summary>
    /// <summary>
    /// The library carries no secret, so plain http is merely unwise rather than dangerous — but a
    /// typo still has to be caught before it becomes a twenty second timeout.
    /// </summary>
    private static bool Reachable(string url, out string problem)
    {
        problem = string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            return true;
        }

        problem = "That is not a URL.";
        return false;
    }

    private bool Begin(StaticProfile profile, string url, out string problem)
    {
        problem = string.Empty;

        if (IsBusy)
        {
            problem = "Already talking to the server.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            problem = "That is not a URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && !parsed.IsLoopback)
        {
            problem = "Only https, or a server on this machine. The password would otherwise be " +
                      "readable by anything between here and there.";
            return false;
        }

        return true;
    }

    private void Start(StaticProfile profile, string message, Func<Task<SyncOutcome>> work)
    {
        if (!string.IsNullOrEmpty(message))
            Message = message;

        failed = false;
        runningFor = profile.Id;
        running = Task.Run(work);
    }

    private void Fail(string message)
    {
        failed = true;
        Message = message;
        Services.Log.Warning($"Sync: {message}");
    }

    private static string Url(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private async Task<T> GetAsync<T>(string baseUrl, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(baseUrl, path));
        return await SendAsync<T>(request, token);
    }

    private async Task<T> PostAsync<T>(string baseUrl, string path, string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, path)) { Content = Body(body) };
        return await SendAsync<T>(request, token);
    }

    private async Task<T> PutAsync<T>(string baseUrl, string path, string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(baseUrl, path)) { Content = Body(body) };
        return await SendAsync<T>(request, token);
    }

    private static StringContent Body(object body) =>
        new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

    private async Task<T> SendAsync<T>(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new SyncException(response.StatusCode, ErrorFrom(text, response.StatusCode));

        var result = JsonConvert.DeserializeObject<T>(text);

        if (result == null)
            throw new SyncException(response.StatusCode, "The server sent something this plugin could not read.");

        return result;
    }

    /// <summary>
    /// The server's own message when it sent one, and a readable fallback when it did not.
    ///
    /// Worth the few lines: "403" on a screen tells somebody nothing, and the difference between a
    /// wrong password and a character nobody has approved yet is the difference between trying again
    /// and asking the admin.
    /// </summary>
    private static string ErrorFrom(string body, HttpStatusCode code)
    {
        try
        {
            if (JsonConvert.DeserializeObject<SyncError>(body) is { } error && !string.IsNullOrEmpty(error.Message))
                return error.Message;
        }
        catch (JsonException)
        {
            // Not json. Fall through to the status code, which is at least true.
        }

        return code switch
        {
            HttpStatusCode.Unauthorized => "The server did not accept this token. Join again.",
            HttpStatusCode.Forbidden => "This character is not allowed to do that.",
            HttpStatusCode.NotFound => "No static by that name on this server.",
            HttpStatusCode.Conflict => "That name is taken.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute.",
            _ => $"The server answered {(int)code}.",
        };
    }

    private static string Explain(Exception? ex) => ex switch
    {
        null => "Something went wrong.",
        SyncException sync => sync.Message,
        TaskCanceledException => "The server did not answer in time.",
        HttpRequestException http => $"Could not reach the server: {http.Message}",

        // A document that will not parse is not a network problem and not the user's fault, and the
        // raw parser message — four lines about JSON arrays and dictionaries — tells them nothing
        // they can act on. What they can act on is pushing over it, so that is what it says.
        JsonException => "The server's copy could not be read. It may have been written by a " +
                         "different version of this plugin. Nothing local was changed — press " +
                         "\"Push now\" to replace it with your copy.",

        _ => ex.Message,
    };

    /// <summary>
    /// A cheap fingerprint of the document, used only to notice that it changed.
    ///
    /// The serialized text itself rather than a hash of parts: it covers every field including ones
    /// added later, and nobody has to remember to extend it. Costs a serialize twice a second while
    /// a synced static is open, which is nothing next to what the draw loop is already doing.
    /// </summary>
    private static string HashOf(SyncDocument document)
    {
        var json = JsonConvert.SerializeObject(document);
        return $"{json.Length}:{json.GetHashCode()}";
    }

    public void Dispose() => http.Dispose();
}

/// <summary>An error the server reported, carried across the task boundary with its status.</summary>
public sealed class SyncException : Exception
{
    public SyncException(HttpStatusCode code, string message) : base(message) => Code = code;

    public HttpStatusCode Code { get; }
}

internal enum SyncOutcomeKind
{
    Claimed,
    Pulled,
    Pushed,
    Members,
    Library,
    Tier,
}

/// <summary>What a finished request produced, waiting to be applied on the framework thread.</summary>
internal sealed record SyncOutcome(
    SyncOutcomeKind Kind,
    SyncClaim? Claim = null,
    SyncEnvelope? Envelope = null,
    List<SyncMember>? Members = null,
    List<TierListing>? Listings = null,
    TierDefinition? Tier = null,
    string Url = "",
    string RemoteName = "",
    string Hash = "")
{
    public static SyncOutcome GotLibrary(List<TierListing> listings) =>
        new(SyncOutcomeKind.Library, Listings: listings);

    public static SyncOutcome GotTier(TierDefinition tier, string id) =>
        new(SyncOutcomeKind.Tier, Tier: tier, RemoteName: id);

    public static SyncOutcome Claimed(SyncClaim claim, string remoteName, string url) =>
        new(SyncOutcomeKind.Claimed, Claim: claim, Url: url, RemoteName: remoteName);

    public static SyncOutcome Pulled(SyncEnvelope envelope) => new(SyncOutcomeKind.Pulled, Envelope: envelope);

    public static SyncOutcome Pushed(SyncEnvelope envelope, string hash) =>
        new(SyncOutcomeKind.Pushed, Envelope: envelope, Hash: hash);

    public static SyncOutcome GotMembers(List<SyncMember> members) =>
        new(SyncOutcomeKind.Members, Members: members);
}
