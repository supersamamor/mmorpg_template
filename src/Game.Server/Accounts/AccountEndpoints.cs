using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Game.Server.Accounts;

/// <summary>
/// The account endpoints (<see cref="AccountPaths"/>) on the gateway's own listener, so its transport rule holds for them too: TLS everywhere, plain HTTP on
/// loopback in development only (ARCHITECTURE §10). It reads bounded bodies, checks every field's kind, and turns <see cref="AccountService"/>'s answers into
/// JSON; nothing here knows a rule.
/// </summary>
public sealed class AccountEndpoints(AccountService service, IServerLog log)
{
    /// <summary>Largest request body, in bytes: a username and the longest password with every character escaped fit.</summary>
    public const int MaxBodyBytes = 2048;

    /// <summary>Longest a request may take to arrive.</summary>
    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Answers the request if it is for an account path; false leaves it to the caller.</summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path != AccountPaths.Root && !path.StartsWith(AccountPaths.Root + "/", StringComparison.Ordinal)) return false;
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        try
        {
            await HandleAsync(context, path).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // the client went away
        }
        catch (Exception e) when (e is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            // The type only: an exception text could quote what the client sent.
            log.Error($"[accounts] {context.Request.Method} {Route(path)} failed: {e.GetType().FullName}\n{e.StackTrace}");
            if (!context.Response.HasStarted) await FailAsync(context, AccountErrors.Unavailable, 2_000).ConfigureAwait(false);
        }
        return true;
    }

    private async Task HandleAsync(HttpContext context, string path)
    {
        var address = AddressKeys.Of(context.Connection.RemoteIpAddress);
        if (!service.AllowRequest(address, out var retry))
        {
            await FailAsync(context, AccountErrors.RateLimited, retry).ConfigureAwait(false);
            return;
        }
        var method = context.Request.Method;
        var cancel = context.RequestAborted;
        switch (Route(path))
        {
            case AccountPaths.Register when HttpMethods.IsPost(method):
            case AccountPaths.Login when HttpMethods.IsPost(method):
            case AccountPaths.Keep when HttpMethods.IsPost(method):
                if (await ReadAsync(context, "username", "password").ConfigureAwait(false) is not [var username, var password])
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                var register = path == AccountPaths.Register;
                var session = register
                    ? await service.RegisterAsync(username, password, address, cancel).ConfigureAwait(false)
                    : path == AccountPaths.Keep
                        ? await service.KeepAsync(username, password, address, cancel).ConfigureAwait(false)
                        : await service.LoginAsync(username, password, address, cancel).ConfigureAwait(false);
                await AnswerAsync(context, session, register ? StatusCodes.Status201Created : StatusCodes.Status200OK, Write).ConfigureAwait(false);
                return;
            case AccountPaths.Password when HttpMethods.IsPost(method):
                if (await ReadAsync(context, "current", "new").ConfigureAwait(false) is not [var current, var next])
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                await AnswerAsync(context, await service.ChangePasswordAsync(Token(context), current, next, address, cancel).ConfigureAwait(false), StatusCodes.Status200OK, Write).ConfigureAwait(false);
                return;
            case AccountPaths.Delete when HttpMethods.IsPost(method):
                if (await ReadAsync(context, "password").ConfigureAwait(false) is not [var proof])
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                await AnswerAsync(context, await service.DeleteAccountAsync(Token(context), proof, address, cancel).ConfigureAwait(false), StatusCodes.Status200OK,
                    (w, erasure) => w.WriteNumber("erased_in", (long)erasure.In.TotalSeconds)).ConfigureAwait(false);
                return;
            case AccountPaths.Logout when HttpMethods.IsPost(method):
                await AnswerAsync(context, await service.LogoutAsync(Token(context), cancel).ConfigureAwait(false), StatusCodes.Status204NoContent, null).ConfigureAwait(false);
                return;
            case AccountPaths.Characters when path == AccountPaths.Characters && HttpMethods.IsGet(method):
                await AnswerAsync(context, await service.ListAsync(Token(context), cancel).ConfigureAwait(false), StatusCodes.Status200OK, (w, list) =>
                {
                    w.WriteNumber("slots", list.Slots);
                    w.WriteNumber("name_min", list.NameMin);
                    w.WriteNumber("name_max", list.NameMax);
                    w.WriteStartArray("characters");
                    foreach (var character in list.Characters)
                    {
                        w.WriteStartObject();
                        Write(w, character);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }).ConfigureAwait(false);
                return;
            case AccountPaths.Characters when path == AccountPaths.Characters && HttpMethods.IsPost(method):
                if (await ReadAsync(context, ParseCreate).ConfigureAwait(false) is not { } create)
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                await AnswerAsync(context, await service.CreateAsync(Token(context), create.Name, create.Appearance, cancel).ConfigureAwait(false), StatusCodes.Status201Created, Write).ConfigureAwait(false);
                return;
            case AccountPaths.Characters when path != AccountPaths.Characters && HttpMethods.IsDelete(method):
                if (!Guid.TryParseExact(path[(AccountPaths.Characters.Length + 1)..], "D", out var id))
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                await AnswerAsync(context, await service.DeleteAsync(Token(context), id, cancel).ConfigureAwait(false), StatusCodes.Status204NoContent, null).ConfigureAwait(false);
                return;
            case null:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
        }
    }

    /// <summary>The endpoint a path belongs to, or null. A path of unknown shape never reaches a log.</summary>
    private static string? Route(string path) => path switch
    {
        AccountPaths.Register or AccountPaths.Login or AccountPaths.Logout or AccountPaths.Password or AccountPaths.Delete or AccountPaths.Keep or AccountPaths.Characters => path,
        _ when path.StartsWith(AccountPaths.Characters + "/", StringComparison.Ordinal) => AccountPaths.Characters,
        _ => null,
    };

    /// <summary>The session token of the <c>Authorization</c> header, or null. It is never taken from the URL, which would end up in logs.</summary>
    private static string? Token(HttpContext context)
    {
        var value = context.Request.Headers.Authorization;
        return value.Count == 1 && value[0] is { } text && text.StartsWith(AccountPaths.TokenScheme, StringComparison.Ordinal) ? text[AccountPaths.TokenScheme.Length..] : null;
    }

    /// <summary>
    /// The string fields of the request's JSON object, in the order asked for, or null: not JSON by its content type, longer than <see cref="MaxBodyBytes"/>, not
    /// arriving in time, not one object, a field missing, given twice or not a string. Unknown fields are ignored.
    /// </summary>
    private static async Task<string[]?> ReadAsync(HttpContext context, params string[] fields) => await ReadAsync(context, utf8 => Parse(utf8, fields)).ConfigureAwait(false);

    private delegate T? BodyParser<T>(ReadOnlySpan<byte> utf8) where T : class;

    private static async Task<T?> ReadAsync<T>(HttpContext context, BodyParser<T> parse) where T : class
    {
        var request = context.Request;
        if (request.ContentType is not { } type || !type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)) return null;
        if (request.ContentLength > MaxBodyBytes) return null;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
        var buffer = new byte[MaxBodyBytes + 1];
        var count = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(BodyTimeout);
        try
        {
            while (count < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            return count > MaxBodyBytes ? null : parse(buffer.AsSpan(0, count));
        }
        catch (Exception e) when (e is BadHttpRequestException || (e is OperationCanceledException && !context.RequestAborted.IsCancellationRequested))
        {
            return null;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); // it may have held a password
        }
    }

    internal static string[]? Parse(ReadOnlySpan<byte> utf8, params string[] fields)
    {
        try
        {
            var values = new string?[fields.Length];
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var at = Array.IndexOf(fields, reader.GetString());
                reader.Read();
                if (at < 0) reader.Skip();
                else if (reader.TokenType != JsonTokenType.String || values[at] is not null) return null;
                else values[at] = reader.GetString();
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return null;
            return Array.IndexOf(values, null) < 0 ? Array.ConvertAll(values, v => v!) : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A creation body: <c>"name"</c> (a string), and optionally <c>"appearance"</c>, an object of optional strings (<c>gender</c>, <c>skin</c>, <c>face</c>, <c>hair</c>,
    /// <c>hair_color</c>; null means left out). Anything of another type, a repeated field, or an unknown field inside the appearance is a bad request; unknown fields outside it
    /// are ignored.
    /// </summary>
    internal static CreateCharacterRequest? ParseCreate(ReadOnlySpan<byte> utf8)
    {
        try
        {
            string? name = null;
            AppearanceChoice? choice = null;
            var seenAppearance = false;
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.GetString();
                reader.Read();
                switch (field)
                {
                    case "name":
                        if (reader.TokenType != JsonTokenType.String || name is not null) return null;
                        name = reader.GetString();
                        break;
                    case "appearance":
                        if (seenAppearance) return null;
                        seenAppearance = true;
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (ParseChoice(ref reader) is not { } parsed) return null;
                        choice = parsed;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || name is null) return null;
            return new CreateCharacterRequest(name, choice);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AppearanceChoice? ParseChoice(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        string? gender = null, skin = null, face = null, hair = null, hairColor = null;
        bool g = false, s = false, f = false, h = false, c = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = reader.GetString();
            reader.Read();
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null)) return null;
            var value = reader.GetString();
            switch (field)
            {
                case "gender" when !g: gender = value; g = true; break;
                case "skin" when !s: skin = value; s = true; break;
                case "face" when !f: face = value; f = true; break;
                case "hair" when !h: hair = value; h = true; break;
                case "hair_color" when !c: hairColor = value; c = true; break;
                default: return null;
            }
        }
        return reader.TokenType == JsonTokenType.EndObject ? new AppearanceChoice(gender, skin, face, hair, hairColor) : null;
    }

    private static void Write(Utf8JsonWriter w, NewSession session)
    {
        w.WriteString("token", session.Token);
        w.WriteNumber("expires_in", (long)session.Lifetime.TotalSeconds);
    }

    private static void Write(Utf8JsonWriter w, CharacterRecord character)
    {
        w.WriteString("id", character.Id);
        w.WriteNumber("slot", character.Slot);
        w.WriteString("name", character.Name);
        w.WriteString("job", character.Job);
        if (character.Appearance is { } looks) ProtocolJson.WriteAppearance(w, "appearance", looks);
    }

    private static Task AnswerAsync<T>(HttpContext context, AccountResult<T> result, int status, Action<Utf8JsonWriter, T>? write) where T : class
    {
        if (!result.Ok) return FailAsync(context, result.Error, result.RetryAfterMs);
        if (write is null)
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        return SendAsync(context, status, w => write(w, result.Value));
    }

    private static Task FailAsync(HttpContext context, string error, long retryAfterMs = 0)
    {
        var seconds = (retryAfterMs + 999) / 1000;
        var erasing = error == AccountErrors.AccountDeleting; // waiting does not help: it says how long there is left to take the deletion back
        // T-806: a ban says how long is left in the body (none: it never ends); trying again sooner does not help, so no Retry-After either.
        if (seconds > 0 && !erasing && error != AccountErrors.AccountBanned) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return SendAsync(context, StatusOf(error), w =>
        {
            w.WriteString("error", error);
            if (erasing) w.WriteNumber("erased_in", seconds);
            else if (seconds > 0) w.WriteNumber("retry_after", seconds);
        });
    }

    /// <summary>The HTTP status that goes with an error.</summary>
    public static int StatusOf(string error) => error switch
    {
        AccountErrors.InvalidCredentials or AccountErrors.InvalidSession => StatusCodes.Status401Unauthorized,
        AccountErrors.UnknownCharacter => StatusCodes.Status404NotFound,
        AccountErrors.UsernameTaken or AccountErrors.NameTaken or AccountErrors.NoFreeSlot or AccountErrors.CharacterInUse or AccountErrors.AccountDeleting or AccountErrors.GuildStorageNotEmpty => StatusCodes.Status409Conflict,
        AccountErrors.AccountBanned => StatusCodes.Status403Forbidden,
        AccountErrors.RateLimited => StatusCodes.Status429TooManyRequests,
        AccountErrors.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    private static async Task SendAsync(HttpContext context, int status, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = buffer.WrittenCount;
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }
}
