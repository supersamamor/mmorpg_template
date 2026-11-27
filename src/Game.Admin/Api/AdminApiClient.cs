using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Game.Admin.Auth;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Admin.Api;

/// <summary>The service credential, read once from the environment. It goes into one request header and nowhere else: not into a page, a log line or an exception.</summary>
public sealed class PanelCredential(string secret)
{
    private readonly string _secret = secret.Trim();

    /// <summary>Puts it on a request to the server.</summary>
    public void Apply(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("Authorization", AdminHeaders.CredentialScheme + _secret);

    /// <inheritdoc />
    public override string ToString() => "PanelCredential { *** }";
}

/// <summary>Why a call has no value: an "Admin errors" term or one of the panel's own (<see cref="ErrorWords"/>).</summary>
/// <param name="Term">The term.</param>
/// <param name="Status">The HTTP status, or 0 when there was no answer.</param>
/// <param name="RequestId">The request's id: the server's audit rows and log carry it.</param>
/// <param name="RetryAfterSeconds">When the server said to try again, or null.</param>
public sealed record ApiFailure(string Term, int Status, string RequestId, int? RetryAfterSeconds = null)
{
    /// <summary>In words.</summary>
    public string Words => ErrorWords.For(Term) + (RetryAfterSeconds is { } s ? $" Try again in {s} s." : string.Empty);
}

/// <summary>A read's value, or why there is none.</summary>
public sealed record ApiResult<T>(T? Value, ApiFailure? Failure) where T : class;

/// <summary>
/// What came of a command: the server's answer (done, accepted or refused), or a failure without one. <see cref="OutcomeUnknown"/> says the command may have been applied
/// although no usable answer came; the panel never sends a command a second time.
/// </summary>
public sealed record CommandResult(AdminCommandAnswer? Answer, ApiFailure? Failure, bool OutcomeUnknown, string RequestId);

/// <summary>
/// The one way the panel calls the server (docs/ADMIN_API.md): the five headers on every request, one attempt (nothing is retried), a timeout, no redirects, no cookies,
/// no proxy and no request logging. Every failure comes back as a value with a term.
/// </summary>
public sealed class AdminApiClient(HttpClient http, PanelCredential credential, PanelSettings settings)
{
    /// <summary>The largest answer read.</summary>
    public const int MaxAnswerBytes = 8 * 1024 * 1024;

    /// <summary>How the API's JSON is written.</summary>
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = false };

    /// <summary>The server's state.</summary>
    public Task<ApiResult<AdminStatusAnswer>> GetStatusAsync(Staff staff, CancellationToken cancel = default) =>
        ReadAsync<AdminStatusAnswer>(staff, AdminPaths.Status, a => a.Instances is not null && a.Errors is not null, cancel);

    /// <summary>The server's commands and their lowest roles.</summary>
    public Task<ApiResult<CommandListAnswer>> GetCommandsAsync(Staff staff, CancellationToken cancel = default) =>
        ReadAsync<CommandListAnswer>(staff, AdminPaths.Commands, a => a.Commands is not null, cancel);

    /// <summary>A page of accounts.</summary>
    public Task<ApiResult<PageAnswer<AccountAnswer>>> FindAccountsAsync(Staff staff, string? name, string? id, string? after, CancellationToken cancel = default) =>
        ReadAsync<PageAnswer<AccountAnswer>>(staff, AdminPaths.Accounts + Query(("name", name), ("id", id), ("after", after)), a => a.Items is not null, cancel);

    /// <summary>One account.</summary>
    public Task<ApiResult<AccountDetailAnswer>> GetAccountAsync(Staff staff, Guid id, CancellationToken cancel = default) =>
        ReadAsync<AccountDetailAnswer>(staff, $"{AdminPaths.Accounts}/{id:D}", a => a.Account is not null && a.Characters is not null, cancel);

    /// <summary>A page of characters.</summary>
    public Task<ApiResult<PageAnswer<CharacterAnswer>>> FindCharactersAsync(Staff staff, string? name, string? id, string? account, string? after, CancellationToken cancel = default) =>
        ReadAsync<PageAnswer<CharacterAnswer>>(staff, AdminPaths.Characters + Query(("name", name), ("id", id), ("account", account), ("after", after)), a => a.Items is not null, cancel);

    /// <summary>One character and its saved state.</summary>
    public Task<ApiResult<CharacterDetailAnswer>> GetCharacterAsync(Staff staff, Guid id, CancellationToken cancel = default) =>
        ReadAsync<CharacterDetailAnswer>(staff, $"{AdminPaths.Characters}/{id:D}", a => a.Character is not null, cancel);

    /// <summary>A page of the audit log.</summary>
    public Task<ApiResult<AuditPageAnswer>> SearchAuditAsync(Staff staff, string? target, string? staffId, string? action, string? from, string? to, string? before,
        CancellationToken cancel = default) =>
        ReadAsync<AuditPageAnswer>(staff, AdminPaths.Audit + Query(("target", target), ("staff", staffId), ("action", action), ("from", from), ("to", to), ("before", before)),
            a => a.Items is not null, cancel);

    /// <summary>True when <paramref name="action"/> can be part of a command's path: <c>a-z 0-9 _</c>.</summary>
    public static bool IsActionName(string? action) => action is { Length: > 0 and <= 64 } && action.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>Sends a command once.</summary>
    public async Task<CommandResult> RunCommandAsync(Staff staff, string action, string jsonBody, CancellationToken cancel = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        if (!IsActionName(action)) return new CommandResult(null, new ApiFailure(AdminErrors.NotFound, 0, requestId), false, requestId);
        using var request = Request(HttpMethod.Post, $"{AdminPaths.Commands}/{action}", staff, requestId);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        var (status, body, retryAfter, failure) = await SendAsync(request, requestId, cancel).ConfigureAwait(false);
        if (failure is not null) return new CommandResult(null, failure, failure.Term != ErrorWords.Untrusted, requestId);
        if (Parse<AdminCommandAnswer>(body) is { Outcome: not null } answer && AdminOutcomes.All.Contains(answer.Outcome)
            && (answer.Outcome == AdminOutcomes.Refused) == (status >= 400))
            return new CommandResult(answer, null, false, requestId);
        if (status >= 400 && ErrorTerm(body) is { } term) return new CommandResult(null, new ApiFailure(term, status, requestId, retryAfter), false, requestId);
        return new CommandResult(null, new ApiFailure(ErrorWords.BadAnswer, status, requestId), true, requestId);
    }

    private async Task<ApiResult<T>> ReadAsync<T>(Staff staff, string path, Func<T, bool> whole, CancellationToken cancel) where T : class
    {
        var requestId = Guid.NewGuid().ToString("N");
        using var request = Request(HttpMethod.Get, path, staff, requestId);
        var (status, body, retryAfter, failure) = await SendAsync(request, requestId, cancel).ConfigureAwait(false);
        if (failure is not null) return new ApiResult<T>(null, failure);
        if (status == 200)
            return Parse<T>(body) is { } value && whole(value) ? new ApiResult<T>(value, null) : new ApiResult<T>(null, new ApiFailure(ErrorWords.BadAnswer, status, requestId));
        return new ApiResult<T>(null, new ApiFailure(ErrorTerm(body) ?? ErrorWords.BadAnswer, status, requestId, retryAfter));
    }

    private HttpRequestMessage Request(HttpMethod method, string path, Staff staff, string requestId)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(settings.ServerUrl), path));
        credential.Apply(request);
        request.Headers.TryAddWithoutValidation(AdminHeaders.StaffId, staff.Id);
        request.Headers.TryAddWithoutValidation(AdminHeaders.StaffName, Uri.EscapeDataString(staff.Name));
        request.Headers.TryAddWithoutValidation(AdminHeaders.StaffRole, staff.Role);
        request.Headers.TryAddWithoutValidation(AdminHeaders.RequestId, requestId);
        return request;
    }

    private async Task<(int Status, byte[] Body, int? RetryAfter, ApiFailure? Failure)> SendAsync(HttpRequestMessage request, string requestId, CancellationToken cancel)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            var retry = response.Headers.RetryAfter?.Delta is { } delta ? (int?)Math.Ceiling(delta.TotalSeconds) : null;
            return ((int)response.StatusCode, body, retry, null);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (0, [], null, new ApiFailure(ErrorWords.NoAnswerInTime, 0, requestId));
        }
        catch (HttpRequestException e)
        {
            var term = e.InnerException is AuthenticationException ? ErrorWords.Untrusted : ErrorWords.Unreachable;
            return (0, [], null, new ApiFailure(term, 0, requestId));
        }
    }

    private static T? Parse<T>(byte[] body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorTerm(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                && IsActionName(e.GetString()) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Query(params (string Name, string? Value)[] parts)
    {
        var set = parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value!.Trim())).ToList();
        return set.Count == 0 ? string.Empty : "?" + string.Join('&', set);
    }

    /// <summary>The handler under the client: nothing follows a redirect or uses a proxy or cookies, and TLS is verified (plus, when set, one pinned certificate).</summary>
    public static SocketsHttpHandler Handler(PanelSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a redirect would carry the staff headers somewhere else
            UseCookies = false,
            UseProxy = false, // the server is on a private network; the credential never goes through a proxy
        };
        if (settings.ServerCertificatePath is { } path)
        {
            var pinned = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => Trusts(pinned, certificate as X509Certificate2, errors, DateTime.Now);
        }
        return handler;
    }

    /// <summary>
    /// True when the system trusts the certificate, or when it is exactly the pinned one, names the server's host and is within its dates. A pinned certificate adds one
    /// certificate; it never turns checking off.
    /// </summary>
    public static bool Trusts(X509Certificate2 pinned, X509Certificate2? presented, SslPolicyErrors errors, DateTime now) =>
        errors == SslPolicyErrors.None
        || (presented is not null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 && presented.RawData.AsSpan().SequenceEqual(pinned.RawData)
            && now >= presented.NotBefore && now <= presented.NotAfter);
}
