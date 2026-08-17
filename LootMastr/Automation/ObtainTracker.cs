using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Automation;

/// <summary>What the tracker saw and what it made of it, kept for the debug tab.</summary>
public readonly record struct ObtainSighting(DateTime At, string Type, string Text, uint ItemId, string Player, string Verdict);

/// <summary>
/// Ticks the need lists off by watching chat for who actually received what.
///
/// Matching goes through the message's payloads, not its wording: the item arrives as an
/// <see cref="ItemPayload"/> carrying its id, so nothing here depends on the client language. The
/// chat types it listens to were narrowed by hand, so every message it considered is recorded in
/// <see cref="Recent"/> for when a client turns out to use a different one.
/// </summary>
public sealed class ObtainTracker : IDisposable
{
    private const int RecentLimit = 40;

    /// <summary>Loot notices. 2110 and 2111 are the obtain and roll lines; system messages carry some too.</summary>
    private static readonly HashSet<XivChatType> LootChannels =
    [
        XivChatType.SystemMessage,
        (XivChatType)2110,
        (XivChatType)2111,
        (XivChatType)62,
        (XivChatType)2105,
    ];

    private readonly Configuration config;
    private readonly RosterStore roster;
    private readonly TierCatalog tiers;
    private readonly LootAssigner assigner;

    private readonly List<ObtainSighting> recent = new();

    public ObtainTracker(Configuration config, RosterStore roster, TierCatalog tiers, LootAssigner assigner)
    {
        this.config = config;
        this.roster = roster;
        this.tiers = tiers;
        this.assigner = assigner;

        Services.Chat.ChatMessage += OnChatMessage;
    }

    public IReadOnlyList<ObtainSighting> Recent => recent;

    /// <summary>Turn off to stop the plugin editing the lists behind your back.</summary>
    public bool Enabled { get; set; } = true;

    public void Clear() => recent.Clear();

    private void OnChatMessage(IChatMessage chat)
    {
        if (!LootChannels.Contains(chat.LogKind))
            return;

        var message = chat.Message;
        var itemId = message.Payloads.OfType<ItemPayload>().FirstOrDefault()?.ItemId ?? 0;
        if (itemId == 0)
            return;

        var text = message.TextValue;
        var member = FindPlayer(message, chat.Sender, text);
        var verdict = Apply(member, itemId);

        Remember(new ObtainSighting(DateTime.Now, chat.LogKind.ToString(), text, itemId,
                                    member?.Name ?? "?", verdict));
    }

    /// <summary>
    /// Who the line is about. A player payload is trusted first; failing that the roster is scanned
    /// for a name inside the text, which is what covers "You obtain…" for the local player too.
    /// </summary>
    private RosterMember? FindPlayer(SeString message, SeString sender, string text)
    {
        var payload = message.Payloads.OfType<PlayerPayload>().FirstOrDefault()
                      ?? sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();

        if (payload != null)
        {
            var byPayload = roster.Find(payload.PlayerName, payload.World.ValueNullable?.Name.ExtractText() ?? string.Empty)
                            ?? roster.Members.FirstOrDefault(m => m.Name == payload.PlayerName);

            if (byPayload != null)
                return byPayload;
        }

        var senderText = sender.TextValue;

        var named = roster.Members.FirstOrDefault(m => !string.IsNullOrEmpty(m.Name) &&
                                                       (senderText.Contains(m.Name, StringComparison.Ordinal) ||
                                                        text.Contains(m.Name, StringComparison.Ordinal)));

        if (named != null)
            return named;

        // "You obtain a Genji ring coffer." names nobody and carries no player payload, which is
        // exactly the line the leader gets when they keep a coffer themselves — a common enough
        // case that leaving it untracked showed up as gear the plugin thought was still owed.
        return payload == null && Services.PlayerState.IsLoaded
                   ? roster.Find(Services.PlayerState.CharacterName,
                                 Services.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty)
                   : null;
    }

    private string Apply(RosterMember? member, uint itemId)
    {
        if (!tiers.TryMatch(itemId, out var slot, out var upgrade))
            return "not tier loot";

        // Ahead of everything else, and regardless of who it went to: an item somebody has obtained
        // is not still in the chest waiting to be assigned. This is the only honest signal there is
        // — pressing the buttons does not mean the game accepted it.
        assigner.MarkHandedOver(itemId);

        if (member == null)
            return "no roster player in the line";

        if (!Enabled)
            return "tracking off";

        var item = new LiveLootItem(-1, itemId, string.Empty, 0, 1, default, default, default, false, 0, slot, upgrade);
        assigner.Record(member, item);

        return $"ticked off for {member.Name}";
    }

    private void Remember(ObtainSighting sighting)
    {
        recent.Insert(0, sighting);

        if (recent.Count > RecentLimit)
            recent.RemoveRange(RecentLimit, recent.Count - RecentLimit);
    }

    public void Dispose() => Services.Chat.ChatMessage -= OnChatMessage;
}
