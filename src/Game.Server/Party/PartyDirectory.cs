using Game.Server.Chat;
using Game.Server.Net;
using Game.Server.World;

namespace Game.Server.Party;

/// <summary>Who plays where, as the party service needs it (T-902). Thread-safe; nothing here reads the database or the tick's state.</summary>
public interface IPartyDirectory
{
    /// <summary>The character <paramref name="character"/> if it plays now (its session has had its welcome), or null: in its grace period, or gone.</summary>
    ChatPeer? Playing(EntityId character);

    /// <summary>The playing character whose name has <paramref name="nameKey"/> (<see cref="ChatNames.Key"/>), or null.</summary>
    ChatPeer? Online(string nameKey);

    /// <summary>True while the server holds <paramref name="character"/>: playing, or in its disconnect grace period. False once it has left the world.</summary>
    bool Holds(EntityId character);

    /// <summary>True when <paramref name="listener"/>'s account blocks <paramref name="speaker"/>'s character (T-901's blocks).</summary>
    bool Blocks(ChatPeer listener, ChatPeer speaker);
}

/// <summary>The directory of a running server: the gateway's characters (<see cref="IChatDirectory"/>, <see cref="Gateway.LinkOf"/>) and chat's block lists.</summary>
public sealed class GatewayPartyDirectory(Gateway gateway, ChatService? chat) : IPartyDirectory
{
    private readonly IChatDirectory _chat = gateway;

    /// <inheritdoc />
    public ChatPeer? Playing(EntityId character) => _chat.Find(character);

    /// <inheritdoc />
    public ChatPeer? Online(string nameKey) => _chat.Online(nameKey);

    /// <inheritdoc />
    public bool Holds(EntityId character) => gateway.LinkOf(character) is not null;

    /// <inheritdoc />
    public bool Blocks(ChatPeer listener, ChatPeer speaker) => chat?.Blocks(listener, speaker) ?? false;
}
