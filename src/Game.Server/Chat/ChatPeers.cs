using Game.Server.World;
using Game.Shared.Protocol;

namespace Game.Server.Chat;

/// <summary>Where chat puts a line for one player: the session's queue. Called on whatever thread chat runs; it only queues.</summary>
public interface IChatSink
{
    /// <summary>Queues <paramref name="message"/>, already serialized as <paramref name="bytes"/> (one serialization per line, whoever gets it).</summary>
    void Post(ServerMessage message, byte[] bytes);
}

/// <summary>
/// A character that plays now (its session has had its welcome), as chat sees it: who it is, which map instance it is on, and where its lines go. <see cref="AccountId"/> and
/// <see cref="CharacterId"/> are null for a dev login, which can then not block, report or be blocked.
/// </summary>
public sealed record ChatPeer(EntityId Character, string Name, Guid? AccountId, Guid? CharacterId, string? MapId, IChatSink Sink);

/// <summary>Who plays where (T-901): the gateway answers from the characters it holds. Thread-safe; nothing here reads the database or the tick's state.</summary>
public interface IChatDirectory
{
    /// <summary>Every playing character on the map instance <paramref name="mapId"/>.</summary>
    IReadOnlyList<ChatPeer> OnMap(string mapId);

    /// <summary>The playing character whose name has <paramref name="nameKey"/> (<see cref="ChatNames.Key"/>), or null: in its grace period counts as not playing.</summary>
    ChatPeer? Online(string nameKey);

    /// <summary>The playing character <paramref name="character"/>, or null.</summary>
    ChatPeer? Find(EntityId character);

    /// <summary>The accounts playing here now (block lists of others may be forgotten).</summary>
    IReadOnlyCollection<Guid> PlayingAccounts();
}

/// <summary>
/// The seam for channels whose members something else knows (T-901): a party (T-902) or a guild (T-904) registers one with <see cref="ChatService.Register"/> under its "Chat channels" term,
/// and chat routes it like the map, with the same mute, rate, text and block rules. Chat never knows what a party is.
/// </summary>
public interface IChannelMembers
{
    /// <summary>
    /// The characters who hear a line <paramref name="speaker"/> says on this channel, the speaker included; empty when the speaker is in none of it (the line is refused as
    /// <c>invalid_channel</c>). Called off the tick, on chat's thread: it must be thread-safe and must not read the tick's state; those not playing now are skipped.
    /// </summary>
    IReadOnlyCollection<EntityId> MembersFor(ChatPeer speaker);
}

/// <summary>How chat compares character names: the stored form's key (NFKC, trimmed, lower case), as names are unique by it (T-701).</summary>
public static class ChatNames
{
    /// <summary>The key of <paramref name="name"/>, or null when it cannot be a name.</summary>
    public static string? Key(string? name)
    {
        if (name is null || name.Length is 0 or > 128) return null;
        try
        {
            var text = name.Trim().Normalize(System.Text.NormalizationForm.FormKC);
            return text.Length == 0 ? null : Game.Shared.Accounts.CharacterNameRules.Key(text);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
