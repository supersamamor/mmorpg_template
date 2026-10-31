using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MobileSpike;

/// <summary>
/// WebSocket echo probe. Sends {"seq","t"} once a second and measures round trip.
/// Records exactly what the socket does across an app pause/resume; it never hides a drop.
/// </summary>
public sealed class EchoClient
{
    const string TrustCertPath = "res://certs/echo-dev.crt";
    const string CertName = "echo.spike.test";

    readonly Action<string> _log;
    WebSocketPeer? _ws;
    WebSocketPeer.State _lastState = WebSocketPeer.State.Closed;
    string _url = "";
    int _seq;
    ulong _nextSendUsec;
    int _firstSeqAfterResume = -1;
    readonly List<double> _rtt = new();
    ulong _pausedAtUsec;
    ulong _resumedAtUsec;
    bool _awaitingFirstEchoAfterResume;
    ulong _reconnectStartUsec;

    public bool AutoReconnect { get; set; }
    public string TlsMode { get; private set; } = "-";
    public int Sent => _seq;
    public int Received { get; private set; }

    public EchoClient(Action<string> log) => _log = log;

    public WebSocketPeer.State State => _ws?.GetReadyState() ?? WebSocketPeer.State.Closed;

    public void Connect(string url)
    {
        Close();
        _url = url;
        _ws = new WebSocketPeer();
        Error err;
        if (url.StartsWith("wss://"))
        {
            TlsOptions tls;
            if (FileAccess.FileExists(TrustCertPath))
            {
                var cert = new X509Certificate();
                cert.Load(TrustCertPath);
                tls = TlsOptions.Client(cert, CertName);
                TlsMode = $"verified (pinned dev cert, name {CertName})";
            }
            else
            {
                tls = TlsOptions.ClientUnsafe();
                TlsMode = "UNVERIFIED (no res://certs/echo-dev.crt in build)";
            }
            err = _ws.ConnectToUrl(url, tls);
        }
        else
        {
            TlsMode = "none (cleartext ws://, debug only)";
            err = _ws.ConnectToUrl(url);
        }
        _log($"connect {url} -> {err}; TLS {TlsMode}");
        _lastState = WebSocketPeer.State.Closed;
    }

    public void Close()
    {
        if (_ws != null && _ws.GetReadyState() == WebSocketPeer.State.Open) _ws.Close(1000, "user");
    }

    public void Poll()
    {
        if (_ws == null) return;
        _ws.Poll();
        var state = _ws.GetReadyState();
        ulong now = Time.GetTicksUsec();
        if (state != _lastState)
        {
            string extra = state == WebSocketPeer.State.Closed
                ? $" code={_ws.GetCloseCode()} reason='{_ws.GetCloseReason()}'" : "";
            string sinceResume = _resumedAtUsec > 0 ? $" ({(now - _resumedAtUsec) / 1000.0:F0} ms after resume)" : "";
            _log($"socket {_lastState} -> {state}{extra}{sinceResume}");
            if (state == WebSocketPeer.State.Open && _reconnectStartUsec > 0)
            {
                _log($"auto-reconnect took {(now - _reconnectStartUsec) / 1000.0:F0} ms");
                _reconnectStartUsec = 0;
            }
            if (state == WebSocketPeer.State.Closed && AutoReconnect && _resumedAtUsec > 0 && _reconnectStartUsec == 0)
            {
                _reconnectStartUsec = now;
                _lastState = state;
                Connect(_url);
                return;
            }
            _lastState = state;
        }
        if (state != WebSocketPeer.State.Open) return;

        while (_ws.GetAvailablePacketCount() > 0)
        {
            var text = _ws.GetPacket().GetStringFromUtf8();
            if (Json.ParseString(text).Obj is not Godot.Collections.Dictionary d) continue;
            int seq = (int)d["seq"];
            double rttMs = (now - (ulong)(long)d["t"]) / 1000.0;
            Received++;
            if (_awaitingFirstEchoAfterResume)
            {
                _awaitingFirstEchoAfterResume = false;
                _log($"first echo after resume: seq {seq}, rtt {rttMs:F0} ms, {(now - _resumedAtUsec) / 1000.0:F0} ms after resume");
            }
            // Echoes of messages sent before a pause include the pause itself; keep them out of RTT stats.
            if (seq >= _firstSeqAfterResume) _rtt.Add(rttMs);
        }
        if (now >= _nextSendUsec)
        {
            _nextSendUsec = now + 1_000_000;
            _ws.SendText($"{{\"seq\":{_seq++},\"t\":{now}}}");
        }
    }

    public void OnPaused()
    {
        _pausedAtUsec = Time.GetTicksUsec();
        _log($"PAUSED, socket {State}, sent {Sent} recv {Received}");
    }

    public void OnResumed()
    {
        _resumedAtUsec = Time.GetTicksUsec();
        double away = _pausedAtUsec > 0 ? (_resumedAtUsec - _pausedAtUsec) / 1e6 : 0;
        _firstSeqAfterResume = _seq;
        _awaitingFirstEchoAfterResume = State == WebSocketPeer.State.Open;
        _log($"RESUMED after {away:F1} s, socket {State} (before first poll)");
    }

    public string Stats()
    {
        if (_rtt.Count == 0) return $"RTT: no samples  sent {Sent} recv {Received}";
        var s = _rtt.OrderBy(x => x).ToArray();
        double P(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
        return $"RTT ms: last {_rtt[^1]:F0}  min {s[0]:F0}  p50 {P(0.5):F0}  p95 {P(0.95):F0}  max {s[^1]:F0}  (n={s.Length})  sent {Sent} recv {Received}";
    }
}
