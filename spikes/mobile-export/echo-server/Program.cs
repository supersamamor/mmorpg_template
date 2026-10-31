// T-005 throwaway echo server. Echoes every WebSocket message back unchanged and logs
// connection lifetimes, so backgrounding behaviour can be read from both ends.
//
//   dotnet run -- [--ws-port 8080] [--wss-port 8443] [--keepalive 10] [--keepalive-timeout 0]
//
// ws://  <lan-ip>:8080/echo   (debug builds only, ARCHITECTURE §10)
// wss:// <lan-ip>:8443/echo   self-signed cert, CN/SAN "echo.spike.test" (see docs/MOBILE_BUILD.md)

using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

const string CertName = "echo.spike.test";

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
int wsPort = cfg.GetValue("ws-port", 8080);
int wssPort = cfg.GetValue("wss-port", 8443);
int keepAlive = cfg.GetValue("keepalive", 10);
int keepAliveTimeout = cfg.GetValue("keepalive-timeout", 0);
string certDir = Path.GetFullPath(cfg.GetValue("cert-dir", Path.Combine(AppContext.BaseDirectory, "../../../certs"))!);
string clientCertOut = Path.GetFullPath(cfg.GetValue("client-cert-out", Path.Combine(certDir, "../../godot/certs/echo-dev.crt"))!);

var cert = LoadOrCreateCert(certDir, clientCertOut);

builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Any, wsPort);
    k.Listen(IPAddress.Any, wssPort, o => o.UseHttps(cert));
});

var app = builder.Build();
var wsOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(keepAlive) };
if (keepAliveTimeout > 0) wsOptions.KeepAliveTimeout = TimeSpan.FromSeconds(keepAliveTimeout);
app.UseWebSockets(wsOptions);

int nextId = 0;
app.MapGet("/", () => "T-005 echo server. Connect a WebSocket to /echo.");
app.Map("/echo", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    int id = Interlocked.Increment(ref nextId);
    string who = $"#{id} {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort} {(ctx.Request.IsHttps ? "wss" : "ws")}";
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var opened = DateTime.UtcNow;
    var last = opened;
    long messages = 0;
    Log($"OPEN   {who}");
    var buffer = new byte[64 * 1024];
    string outcome;
    try
    {
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                outcome = $"client close {(int?)r.CloseStatus} '{r.CloseStatusDescription}'";
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                break;
            }
            var gap = DateTime.UtcNow - last;
            if (gap > TimeSpan.FromSeconds(3)) Log($"GAP    {who} {gap.TotalSeconds:F1}s without messages");
            last = DateTime.UtcNow;
            messages++;
            await ws.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ctx.RequestAborted);
        }
    }
    catch (Exception e)
    {
        outcome = $"{e.GetType().Name}: {e.Message}";
    }
    Log($"CLOSE  {who} after {(DateTime.UtcNow - opened).TotalSeconds:F1}s, {messages} msgs, last msg {(DateTime.UtcNow - last).TotalSeconds:F1}s ago: {outcome}");
});

Log($"ws://0.0.0.0:{wsPort}/echo  wss://0.0.0.0:{wssPort}/echo  keepalive={keepAlive}s timeout={(keepAliveTimeout > 0 ? keepAliveTimeout + "s" : "off")}");
Log($"client trust cert: {clientCertOut}");
app.Run();

static void Log(string msg) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");

// Self-signed cert for CN=echo.spike.test. The phone connects by LAN IP and the client
// overrides the expected name, so the cert does not depend on the PC's address.
// The private key stays in certDir (gitignored); only the public cert is written for the client.
static X509Certificate2 LoadOrCreateCert(string certDir, string clientCertOut)
{
    Directory.CreateDirectory(certDir);
    string pfxPath = Path.Combine(certDir, "echo-dev.pfx");
    X509Certificate2 cert;
    if (File.Exists(pfxPath))
    {
        cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
    }
    else
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={CertName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(CertName);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pfx = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(pfxPath, pfx);
        cert = X509CertificateLoader.LoadPkcs12(pfx, null);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(clientCertOut)!);
    File.WriteAllText(clientCertOut, cert.ExportCertificatePem() + "\n");
    return cert;
}
