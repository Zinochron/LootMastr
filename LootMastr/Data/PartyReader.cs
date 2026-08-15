using System.Collections.Generic;
using LootMastr.Roster;

namespace LootMastr.Data;

public readonly record struct PartyPlayer(
    string Name,
    string World,
    uint JobId,
    ulong ContentId,
    bool IsLeader,
    bool IsLocalPlayer)
{
    public string Key => RosterKey.For(Name, World);
}

/// <summary>Reads the current party. Nothing here is cached; the party changes without notice.</summary>
public sealed class PartyReader
{
    /// <summary>
    /// The party as the game sees it right now. Outside a party this is the local player alone,
    /// counted as leader, so everything downstream has one consistent shape to work with.
    /// </summary>
    public List<PartyPlayer> Read()
    {
        var result = new List<PartyPlayer>(8);
        var localName = LocalName();

        if (Services.Party.Length == 0)
        {
            if (Services.PlayerState.IsLoaded)
            {
                result.Add(new PartyPlayer(
                               localName,
                               Services.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                               Services.PlayerState.ClassJob.RowId,
                               Services.PlayerState.ContentId,
                               IsLeader: true,
                               IsLocalPlayer: true));
            }

            return result;
        }

        var leaderIndex = (int)Services.Party.PartyLeaderIndex;

        for (var i = 0; i < Services.Party.Length; i++)
        {
            var member = Services.Party[i];
            if (member == null)
                continue;

            var name = member.Name.TextValue;

            result.Add(new PartyPlayer(
                           name,
                           member.World.ValueNullable?.Name.ExtractText() ?? string.Empty,
                           member.ClassJob.RowId,
                           (ulong)member.ContentId,
                           IsLeader: i == leaderIndex,
                           IsLocalPlayer: name == localName));
        }

        return result;
    }

    /// <summary>
    /// Whether the local player can hand out loot at all. Only the leader gets the recipient
    /// choice, so everything in <c>Automation</c> is gated on this.
    /// </summary>
    public bool LocalPlayerIsLeader()
    {
        if (Services.Party.Length == 0)
            return true;

        var leaderIndex = (int)Services.Party.PartyLeaderIndex;
        if (leaderIndex < 0 || leaderIndex >= Services.Party.Length)
            return false;

        var leader = Services.Party[leaderIndex];
        return leader != null && leader.Name.TextValue == LocalName();
    }

    private static string LocalName() =>
        Services.PlayerState.IsLoaded ? Services.PlayerState.CharacterName : string.Empty;
}
