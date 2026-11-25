using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Game.Shared.Protocol;

namespace Game.Client.Core.Accounts;

/// <summary>Which account endpoint a request is for.</summary>
public enum AccountCall
{
    /// <summary>Create an account and sign in.</summary>
    Register,

    /// <summary>Sign in.</summary>
    SignIn,

    /// <summary>Sign out.</summary>
    SignOut,

    /// <summary>List the characters.</summary>
    List,

    /// <summary>Create a character.</summary>
    Create,

    /// <summary>Delete a character.</summary>
    Delete,
}

/// <summary>One request to the account endpoints. It may hold a password or a token: never log one, and drop it once sent.</summary>
public sealed class AccountHttpRequest
{
    /// <summary>Creates a request.</summary>
    public AccountHttpRequest(AccountCall call, string method, string url, byte[]? body, string? token)
    {
        Call = call;
        Method = method;
        Url = url;
        Body = body;
        Token = token;
    }

    /// <summary>What it is for.</summary>
    public AccountCall Call { get; }

    /// <summary><c>GET</c>, <c>POST</c> or <c>DELETE</c>.</summary>
    public string Method { get; }

    /// <summary>The full URL.</summary>
    public string Url { get; }

    /// <summary>The JSON body, or null.</summary>
    public byte[]? Body { get; }

    /// <summary>The session token for the <c>Authorization</c> header, or null.</summary>
    public string? Token { get; }
}

/// <summary>The answer to a request.</summary>
public readonly struct AccountHttpResponse
{
    /// <summary>Creates an answer.</summary>
    public AccountHttpResponse(int status, byte[] body)
    {
        Status = status;
        Body = body;
    }

    /// <summary>The HTTP status, or 0 when the server could not be reached (or its answer could not be used).</summary>
    public int Status { get; }

    /// <summary>The body.</summary>
    public byte[] Body { get; }
}

/// <summary>The HTTP seam: one request at a time, called and polled on the main thread. It follows no redirect (a token must not travel to another address).</summary>
public interface IAccountTransport
{
    /// <summary>Sends a request; its answer comes out of <see cref="TryDequeue"/>.</summary>
    void Send(AccountHttpRequest request);

    /// <summary>The answer, once there is one.</summary>
    bool TryDequeue(out AccountHttpResponse response);
}

/// <summary>The client's half of the account endpoints' JSON (the server's half is <c>AccountEndpoints</c>). By hand, like <c>ProtocolJson</c>: no reflection.</summary>
public static class AccountJson
{
    /// <summary>The body of a registration or a sign-in.</summary>
    public static byte[] Write(CredentialsRequest request) => Object(w =>
    {
        w.WriteString("username", request.Username);
        w.WriteString("password", request.Password);
    });

    /// <summary>The body of a character creation.</summary>
    public static byte[] Write(CreateCharacterRequest request) => Object(w => w.WriteString("name", request.Name));

    /// <summary>Reads a new session, or null.</summary>
    public static SessionAnswer? ReadSession(byte[] utf8) => Read(utf8, root =>
        root.GetProperty("token").GetString() is { Length: > 0 } token ? new SessionAnswer(token, root.GetProperty("expires_in").GetInt64()) : null);

    /// <summary>Reads the character list, or null.</summary>
    public static CharactersAnswer? ReadCharacters(byte[] utf8) => Read(utf8, root =>
    {
        var list = new List<CharacterRecord>();
        foreach (var item in root.GetProperty("characters").EnumerateArray()) list.Add(Character(item));
        return new CharactersAnswer(root.GetProperty("slots").GetInt32(), root.GetProperty("name_min").GetInt32(), root.GetProperty("name_max").GetInt32(), list);
    });

    /// <summary>Reads a refusal, or null.</summary>
    public static AccountErrorAnswer? ReadError(byte[] utf8) => Read(utf8, root =>
        root.GetProperty("error").GetString() is { Length: > 0 } error ? new AccountErrorAnswer(error, root.TryGetProperty("retry_after", out var retry) ? retry.GetInt64() : 0) : null);

    private static CharacterRecord Character(JsonElement item) =>
        new CharacterRecord(Text(item, "id"), item.GetProperty("slot").GetInt32(), Text(item, "name"), Text(item, "job"));

    private static string Text(JsonElement item, string field) => item.GetProperty(field).GetString() ?? throw new FormatException(field);

    private static T? Read<T>(byte[] utf8, Func<JsonElement, T?> read) where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : null;
        }
        catch (Exception e) when (e is JsonException || e is KeyNotFoundException || e is InvalidOperationException || e is FormatException)
        {
            return null; // not the answer of an account endpoint
        }
    }

    private static byte[] Object(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
