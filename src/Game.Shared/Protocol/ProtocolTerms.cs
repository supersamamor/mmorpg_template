using Game.Shared.Vocabulary;

namespace Game.Shared.Protocol
{
    /// <summary>The wire protocol's version. A client sends it in <c>hello</c>; any other value is refused with <c>bye</c> <c>bad_version</c>.</summary>
    public static class ProtocolVersion
    {
        /// <summary>The version this build speaks. Bump it on any change to a message's shape.</summary>
        public const int Current = 1;
    }

    /// <summary>Message type names, the <c>type</c> field of every message (VOCABULARY.md "Message types").</summary>
    public static class MessageTypes
    {
        /// <summary><c>hello</c> (client → server, first message): protocol version, content hash, and a dev display name or a resume token.</summary>
        public const string Hello = "hello";

        /// <summary><c>move</c> (client → server): walk to a cell.</summary>
        public const string Move = "move";

        /// <summary><c>stop</c> (client → server): stop walking at the next cell.</summary>
        public const string Stop = "stop";

        /// <summary><c>ping</c> (client → server): keep-alive and round-trip probe.</summary>
        public const string Ping = "ping";

        /// <summary><c>logout</c> (client → server): leave on purpose; the character is removed at once, without a grace period.</summary>
        public const string Logout = "logout";

        /// <summary><c>welcome</c> (server → client): the client's entity, map, the server tick rate and a new resume token.</summary>
        public const string Welcome = "welcome";

        /// <summary><c>snapshot</c> (server → client): everything the client sees; replaces its view (also sent on a map change).</summary>
        public const string Snapshot = "snapshot";

        /// <summary><c>delta</c> (server → client): one tick's changes relative to the last snapshot or delta.</summary>
        public const string Delta = "delta";

        /// <summary><c>rejected</c> (server → client): an intent was refused.</summary>
        public const string Rejected = "rejected";

        /// <summary><c>pong</c> (server → client): answer to <c>ping</c>.</summary>
        public const string Pong = "pong";

        /// <summary><c>bye</c> (server → client, last message): why the server is closing the connection.</summary>
        public const string Bye = "bye";

        /// <summary>Types a client may send.</summary>
        public static readonly VocabularySet ClientToServer = new VocabularySet(Hello, Move, Stop, Ping, Logout);

        /// <summary>Types the server sends.</summary>
        public static readonly VocabularySet ServerToClient = new VocabularySet(Welcome, Snapshot, Delta, Rejected, Pong, Bye);

        /// <summary>Every message type, client → server first.</summary>
        public static readonly VocabularySet All = VocabularySet.Union(ClientToServer, ServerToClient);
    }

    /// <summary>Reasons in a <c>bye</c> message (VOCABULARY.md "Bye reasons").</summary>
    public static class ByeReasons
    {
        /// <summary><c>timeout</c>: no <c>hello</c> in time, or no message at all for too long.</summary>
        public const string Timeout = "timeout";

        /// <summary><c>bad_version</c>: <c>hello</c> carried another protocol version.</summary>
        public const string BadVersion = "bad_version";

        /// <summary><c>malformed</c>: not a JSON object, a binary frame, or a known type with missing or wrong fields.</summary>
        public const string Malformed = "malformed";

        /// <summary><c>unknown_type</c>: a <c>type</c> the server does not accept from clients.</summary>
        public const string UnknownType = "unknown_type";

        /// <summary><c>oversize</c>: a message longer than the server's cap.</summary>
        public const string Oversize = "oversize";

        /// <summary><c>before_hello</c>: another message came before <c>hello</c>.</summary>
        public const string BeforeHello = "before_hello";

        /// <summary><c>duplicate_hello</c>: a second <c>hello</c> on a session that already joined.</summary>
        public const string DuplicateHello = "duplicate_hello";

        /// <summary><c>rate_limit</c>: the client kept sending faster than its rate limit allows.</summary>
        public const string RateLimit = "rate_limit";

        /// <summary><c>login_refused</c>: the login was refused (the dev login outside development, a bad name).</summary>
        public const string LoginRefused = "login_refused";

        /// <summary><c>server_full</c>: the server has as many sessions as it accepts.</summary>
        public const string ServerFull = "server_full";

        /// <summary><c>too_slow</c>: the client did not read what it was sent for too long.</summary>
        public const string TooSlow = "too_slow";

        /// <summary><c>shutdown</c>: the server is stopping.</summary>
        public const string Shutdown = "shutdown";

        /// <summary><c>update_required</c>: the client's content hash differs from the server's, or it sent none outside development (ARCHITECTURE §5.4).</summary>
        public const string UpdateRequired = "update_required";

        /// <summary><c>server_error</c>: the server failed to handle the session.</summary>
        public const string ServerError = "server_error";

        /// <summary><c>logout</c>: the client sent <c>logout</c>; its character left the world. Do not reconnect.</summary>
        public const string Logout = "logout";

        /// <summary><c>replaced</c>: another connection resumed this character (the same player on a new socket). Do not reconnect.</summary>
        public const string Replaced = "replaced";

        /// <summary><c>resume_failed</c>: the resume token is unknown, already used, or its character's grace period is over. Log in again.</summary>
        public const string ResumeFailed = "resume_failed";

        /// <summary>Every bye reason.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Timeout, BadVersion, Malformed, UnknownType, Oversize, BeforeHello, DuplicateHello, RateLimit, LoginRefused, ServerFull, TooSlow,
            Shutdown, ServerError, UpdateRequired, Logout, Replaced, ResumeFailed);
    }

    /// <summary>Entity kinds in a view record's <c>kind</c> field (VOCABULARY.md "Entity kinds").</summary>
    public static class EntityKinds
    {
        /// <summary><c>player</c>.</summary>
        public const string Player = "player";

        /// <summary><c>monster</c>.</summary>
        public const string Monster = "monster";

        /// <summary><c>npc</c>.</summary>
        public const string Npc = "npc";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Player, Monster, Npc);
    }
}
