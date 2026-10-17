using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>A friend's party that anyone on their friend list can join. From their chat presence (`lol.pty`, `lol.ptyType` = open).</summary>
public sealed record OpenParty(string FriendName, string Puuid, string PartyId, int QueueId, int Members, int MaxPlayers);

public sealed record PendingInvitation(string InvitationId, string FromName, int QueueId, bool CanAccept);

public static class Parties
{
    /// <summary>`GET /lol-chat/v1/friends`: friends whose party is open and not full. Presence strings are untrusted, so anything malformed is skipped.</summary>
    public static List<OpenParty> OpenFrom(JsonNode? friends)
    {
        var result = new List<OpenParty>();
        if (friends is not JsonArray list) return result;
        foreach (var f in list)
        {
            if (f?["lol"]?["ptyType"]?.GetValue<string>() != "open" || f["lol"]?["pty"]?.GetValue<string>() is not { Length: > 0 } pty) continue;
            try
            {
                if (JsonNode.Parse(pty) is not JsonObject p || p["isPartyOpen"]?.GetValue<bool>() != true || p["partyId"]?.GetValue<string>() is not { Length: > 0 } partyId) continue;
                var members = (p["summonerPuuids"] as JsonArray)?.Count ?? 1;
                var max = p["maxPlayers"]?.GetValue<int>() ?? 5;
                if (members >= max) continue;
                var name = $"{f["gameName"]?.GetValue<string>()}#{f["gameTag"]?.GetValue<string>()}";
                result.Add(new OpenParty(name, f["puuid"]?.GetValue<string>() ?? "", partyId, p["queueId"]?.GetValue<int>() ?? 0, members, max));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        }
        return result;
    }

    /// <summary>`GET /lol-lobby/v2/received-invitations`: only the ones still waiting for an answer.</summary>
    public static List<PendingInvitation> PendingFrom(JsonNode? invitations) =>
        invitations is JsonArray list
            ? [.. list.Where(i => i?["state"]?.GetValue<string>() == "Pending" && i["invitationId"]?.GetValue<string>() is { Length: > 0 })
                .Select(i => new PendingInvitation(i!["invitationId"]!.GetValue<string>(), i["fromSummonerName"]?.GetValue<string>() ?? "A friend",
                    i["gameConfig"]?["queueId"]?.GetValue<int>() ?? 0, i["canAcceptInvitation"]?.GetValue<bool>() ?? true))]
            : [];
}
