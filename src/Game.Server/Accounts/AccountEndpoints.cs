using System.Buffers;
using System.Globalization;
using System.Text.Json;
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
                if (await ReadAsync(context, "username", "password").ConfigureAwait(false) is not [var username, var password])
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                var register = path == AccountPaths.Register;
                var session = register
                    ? await service.RegisterAsync(username, password, address, cancel).ConfigureAwait(false)
                    : await service.LoginAsync(username, password, address, cancel).ConfigureAwait(false);
                await AnswerAsync(context, session, register ? StatusCodes.Status201Created : StatusCodes.Status200OK, (w, s) =>
                {
                    w.WriteString("token", s.Token);
                    w.WriteNumber("expires_in", (long)s.Lifetime.TotalSeconds);
                }).ConfigureAwait(false);
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
                if (await ReadAsync(context, "name").ConfigureAwait(false) is not [var name])
                {
                    await FailAsync(context, AccountErrors.InvalidRequest).ConfigureAwait(false);
                    return;
                }
                await AnswerAsync(context, await service.CreateAsync(Token(context), name, cancel).ConfigureAwait(false), StatusCodes.Status201Created, Write).ConfigureAwait(false);
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
        AccountPaths.Register or AccountPaths.Login or AccountPaths.Logout or AccountPaths.Characters => path,
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
    private static async Task<string[]?> ReadAsync(HttpContext context, params string[] fields)
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
            return count > MaxBodyBytes ? null : Parse(buffer.AsSpan(0, count), fields);
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

    private static void Write(Utf8JsonWriter w, CharacterRecord character)
    {
        w.WriteString("id", character.Id);
        w.WriteNumber("slot", character.Slot);
        w.WriteString("name", character.Name);
        w.WriteString("job", character.Job);
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
        if (seconds > 0) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return SendAsync(context, StatusOf(error), w =>
        {
            w.WriteString("error", error);
            if (seconds > 0) w.WriteNumber("retry_after", seconds);
        });
    }

    /// <summary>The HTTP status that goes with an error.</summary>
    public static int StatusOf(string error) => error switch
    {
        AccountErrors.InvalidCredentials or AccountErrors.InvalidSession => StatusCodes.Status401Unauthorized,
        AccountErrors.UnknownCharacter => StatusCodes.Status404NotFound,
        AccountErrors.UsernameTaken or AccountErrors.NameTaken or AccountErrors.NoFreeSlot or AccountErrors.CharacterInUse => StatusCodes.Status409Conflict,
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
