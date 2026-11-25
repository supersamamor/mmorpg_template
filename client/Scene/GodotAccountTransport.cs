using Game.Client.Core.Accounts;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The account endpoints over Godot's <see cref="HttpRequest"/>: the engine's own TLS, as the game socket uses. No redirects (a token must not follow one), a
/// bounded answer, a timeout. The request's body, which may hold a password, is wiped once the answer is in.
/// </summary>
public sealed class GodotAccountTransport : IAccountTransport
{
    private const int MaxAnswerBytes = 64 * 1024;

    private readonly HttpRequest _http;
    private byte[]? _body;
    private AccountHttpResponse? _answer;

    /// <summary>Creates the transport under <paramref name="parent"/>.</summary>
    public GodotAccountTransport(Node parent, double timeoutSeconds)
    {
        _http = new HttpRequest { Timeout = timeoutSeconds, MaxRedirects = 0, BodySizeLimit = MaxAnswerBytes, AcceptGZip = false };
        parent.AddChild(_http);
        _http.RequestCompleted += OnCompleted;
    }

    /// <inheritdoc />
    public void Send(AccountHttpRequest request)
    {
        _answer = null;
        _body = request.Body;
        var headers = new List<string> { "Accept: application/json" };
        if (request.Body is not null) headers.Add("Content-Type: application/json");
        if (request.Token is not null) headers.Add("Authorization: " + AccountPaths.TokenScheme + request.Token);
        var method = request.Method switch { "POST" => Godot.HttpClient.Method.Post, "DELETE" => Godot.HttpClient.Method.Delete, _ => Godot.HttpClient.Method.Get };
        if (_http.RequestRaw(request.Url, headers.ToArray(), method, request.Body ?? []) != Error.Ok) Finish(0, []);
    }

    /// <inheritdoc />
    public bool TryDequeue(out AccountHttpResponse response)
    {
        response = _answer ?? default;
        if (_answer is null) return false;
        _answer = null;
        return true;
    }

    private void OnCompleted(long result, long code, string[] headers, byte[] body) => Finish(result == (long)HttpRequest.Result.Success ? (int)code : 0, body);

    private void Finish(int status, byte[] body)
    {
        if (_body is not null) Array.Clear(_body);
        _body = null;
        _answer = new AccountHttpResponse(status, body);
    }
}
