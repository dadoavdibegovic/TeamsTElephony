# .NET Compliance Recording bot — implementation guide

Companion to `docs/bot-contractor-spec.md`. The spec defines **what**
to build and the contract with our Node.js backend. This document is
**how** — code-level guidance, starter project structure, key SDK calls,
local dev setup, deployment.

Audience: the .NET contractor.

---

## 1. Start from Microsoft's reference sample

Don't write this from scratch. Microsoft provides a working Compliance
Recording bot in the official samples repo. Clone it, build it, run
it locally first to internalise the moving parts. Only then start
adapting.

Repo: https://github.com/microsoftgraph/microsoft-graph-comms-samples

Specifically the sample at:
```
Samples/V1.0Samples/LocalMediaSamples/ComplianceRecordingBot
```

Read the README in that directory carefully. It walks through
registering an app, granting permissions, configuring the bot — most
of which we've already done. You can skip those sections and focus
on the code.

The sample writes recorded audio to .wav files. **You don't.** You
forward audio frames to our Node.js backend over a WebSocket
(see section 5 below).

---

## 2. NuGet packages

These are the load-bearing ones. Versions may have moved; pick latest
stable.

```xml
<PackageReference Include="Microsoft.Graph.Communications.Calls" Version="1.2.*" />
<PackageReference Include="Microsoft.Graph.Communications.Calls.Media" Version="1.2.*" />
<PackageReference Include="Microsoft.Graph.Communications.Client" Version="1.2.*" />
<PackageReference Include="Microsoft.Graph.Communications.Common" Version="1.2.*" />
<PackageReference Include="Microsoft.Skype.Bots.Media" Version="1.32.*" />
<PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" Version="*" />
<PackageReference Include="Microsoft.ApplicationInsights.AspNetCore" Version="*" />
<PackageReference Include="Azure.Identity" Version="*" />
<PackageReference Include="Azure.Security.KeyVault.Secrets" Version="*" />
```

Notes:
- `Microsoft.Skype.Bots.Media` ships the native media binaries — this
  forces the project to target Windows OR Linux x64 specifically. Add
  `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>` if deploying to
  Linux App Service.
- The Graph Communications libs are *not* the same as
  `Microsoft.Graph` (the general Graph SDK). They have their own
  versioning track and shipped under the
  `Microsoft.Graph.Communications.*` namespace.

---

## 3. Project structure

A reasonable layout:

```
ComplianceRecordingBot.sln
  ComplianceRecordingBot/
    Program.cs                       # ASP.NET Core host + bot startup
    Startup.cs                       # DI config
    appsettings.json                 # base config (non-secret)
    appsettings.Development.json     # local overrides
    Controllers/
      PlatformCallController.cs      # POST /api/calling — Graph notifications
      HealthController.cs            # GET  /health
    Bot/
      ComplianceRecordingBot.cs      # IGraphLogger, ICommunicationsClient setup
      CallHandler.cs                 # per-call lifecycle + state
      BotMediaStream.cs              # audio sink, forwards to backend WS
      BackendWebSocketClient.cs      # our addition — handles the WS forward
    Authentication/
      AuthenticationProvider.cs      # Graph Communications JWT validation
    Configuration/
      BotConfig.cs                   # strongly-typed config
    Utils/
      Telemetry.cs                   # App Insights helpers
```

---

## 4. Configuration (read from App Service settings, never hardcode)

`appsettings.json` example (just the structure — values come from env at runtime):

```json
{
  "Bot": {
    "AppId": "",
    "TenantId": "",
    "AppSecret": "",
    "ServiceCname": "bot-calltranskript-net.azurewebsites.net",
    "CallingWebHookEndpoint": "https://bot-calltranskript-net.azurewebsites.net/api/calling",
    "MediaInstanceCapacity": 20,
    "MediaServiceCertSubject": ""
  },
  "Backend": {
    "IngestWss": "wss://app-calltranskript-backend.azurewebsites.net/bot/audio",
    "IngestSecret": ""
  },
  "ApplicationInsights": {
    "ConnectionString": ""
  }
}
```

Map to env vars via standard .NET configuration:
- `BOT__APPID` → `Bot.AppId` (already set per spec)
- `BACKEND__INGESTSECRET` → `Backend.IngestSecret` (KV-referenced)

Use `IConfiguration` to bind, not raw `Environment.GetEnvironmentVariable`.

---

## 5. The unique part — audio forwarding to our backend

This is **the** thing you write that's not in the Microsoft sample.
Everything else in the sample stays largely as-is.

### Where to hook in

In Microsoft's sample, `BotMediaStream` receives audio buffers via the
`AudioMediaReceived` event. The sample writes those to a .wav file.
Replace the file write with a WebSocket send to our backend.

### Suggested implementation

```csharp
// Bot/BackendWebSocketClient.cs
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class BackendWebSocketClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly Uri _endpoint;
    private readonly string _correlationId;
    private readonly CancellationTokenSource _cts = new();

    public BackendWebSocketClient(string baseWss, string correlationId, string bearer)
    {
        _correlationId = correlationId;
        _endpoint = new Uri($"{baseWss.TrimEnd('/')}/{Uri.EscapeDataString(correlationId)}");
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
    }

    public async Task ConnectAsync()
    {
        await _ws.ConnectAsync(_endpoint, _cts.Token);
    }

    public async Task SendCallStartedAsync(string? callerPhone, string? callerDisplayName, string? agentUpn)
    {
        var msg = new {
            type = "call.started",
            correlationId = _correlationId,
            callerPhone,
            callerDisplayName,
            agentUpn,
            startedAt = DateTime.UtcNow.ToString("o")
        };
        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
    }

    public async Task SendAudioFrameAsync(byte speakerTag, ReadOnlyMemory<byte> pcm)
    {
        // [1 byte speaker][N bytes PCM]
        var buffer = new byte[1 + pcm.Length];
        buffer[0] = speakerTag;
        pcm.CopyTo(buffer.AsMemory(1));
        await _ws.SendAsync(buffer, WebSocketMessageType.Binary, true, _cts.Token);
    }

    public async Task SendCallEndedAsync(string reason = "normal")
    {
        var msg = new {
            type = "call.ended",
            correlationId = _correlationId,
            endedAt = DateTime.UtcNow.ToString("o"),
            reason
        };
        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (_ws.State == WebSocketState.Open)
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "call.ended", _cts.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _ws.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
```

### Speaker identification

Microsoft's media SDK gives you separate audio buffers per participant.
You need to identify which buffer is the agent (the Teams user with the
recording policy) and which is the caller. Match against the participant
list on the call:
- The participant whose identity matches the recorded user's Teams identity → agent
- Anyone else who's not your bot → caller

If multiple non-agent participants exist (3-way call, etc.), pick the
first one as "caller" or extend the protocol with multi-speaker tags
(coordinate with us before doing this).

### Sample rates

Microsoft's media SDK delivers audio at 16 kHz, 16-bit, mono. **Don't
resample.** That's exactly what our backend expects. Pass through.

If you get other formats from a specific Teams scenario, convert to
16 kHz mono before forwarding.

---

## 6. The CallHandler / call lifecycle

Per-call instance. Wire it like this:

```csharp
// Bot/CallHandler.cs (sketch)
public class CallHandler : HeartbeatHandler
{
    private readonly ICall _call;
    private readonly BotMediaStream _mediaStream;
    private readonly BackendWebSocketClient _backendWs;
    private readonly string _correlationId;

    public CallHandler(ICall call, BotConfig config, ILogger logger)
        : base(TimeSpan.FromMinutes(10), call.GraphLogger)
    {
        _call = call;
        _correlationId = call.Id; // Graph Communications callId is stable for the call

        // Identify the recorded agent — the participant with our policy
        var agentParticipant = call.Participants
            .FirstOrDefault(p => p.Resource?.Info?.Identity?.User != null
                              && p.Resource.Info.Identity.User.Id != config.BotAppId);
        var agentUpn = agentParticipant?.Resource?.Info?.Identity?.User?.AdditionalData
            ?.GetValueOrDefault("userPrincipalName") as string;

        // Identify the caller — best-effort from PSTN identity
        var callerParticipant = call.Participants
            .FirstOrDefault(p => p.Resource?.Info?.Identity?.AdditionalData
                ?.ContainsKey("phone") == true);

        _backendWs = new BackendWebSocketClient(
            config.BackendIngestWss,
            _correlationId,
            config.BackendIngestSecret);

        _ = ConnectBackendAndAnnounceAsync(callerParticipant, agentUpn);

        _mediaStream = new BotMediaStream(
            _call.GetLocalMediaSession().AudioSocket,
            _backendWs,
            call.GraphLogger);

        _call.OnUpdated += OnCallUpdated;
    }

    private async Task ConnectBackendAndAnnounceAsync(IParticipant? caller, string? agentUpn)
    {
        await _backendWs.ConnectAsync();
        await _backendWs.SendCallStartedAsync(
            callerPhone: ExtractPhone(caller),
            callerDisplayName: ExtractDisplayName(caller),
            agentUpn: agentUpn);
    }

    private async void OnCallUpdated(ICall sender, ResourceEventArgs<Call> e)
    {
        if (e.NewResource.State == CallState.Terminated)
        {
            await _backendWs.SendCallEndedAsync("normal");
            await _backendWs.DisposeAsync();
            _mediaStream?.Dispose();
        }
    }
}
```

The exact event names and properties depend on the Communications SDK
version. Cross-reference with the official sample.

---

## 7. Local development setup

You need a public HTTPS URL to receive Graph callbacks. Options:

### Option A: dev tunnels (Microsoft, free, easiest)

```sh
winget install Microsoft.devtunnel   # or download CLI
devtunnel user login
devtunnel create --allow-anonymous
devtunnel port create -p 9442 --protocol https
devtunnel host
```

Note the printed `https://...devtunnels.ms` URL. Set this in your
`appsettings.Development.json` as `Bot.CallingWebHookEndpoint` and
ALSO update the Azure Bot Service registration to point at it for
the duration of testing:

```sh
az bot msteams create \
  --resource-group Call_Transkript_Infra \
  --name bot-calltranskript-prod \
  --enable-calling true \
  --calling-web-hook "https://<your-devtunnel>.devtunnels.ms/api/calling"
```

Don't forget to set it back to the production URL when done with
local dev.

### Option B: ngrok

Similar; just slower. Has a paid tier for stable URLs.

### Local test call

1. Assign the recording policy to **yourself** as the test user (see
   `compliance-recording-policy.md`)
2. Have a colleague call your Teams account from any number
3. Bot should be invoked via Graph notification
4. Watch the .NET console + our Node.js backend's `/health` for events

---

## 8. Deploy to Azure App Service

The contractor provisions and deploys the .NET service. We provide
guidance on what App Service shape works:

- **Plan**: Linux, P1v3 or higher (P0v3 may be too small for media
  workload; concurrent calls × audio decoding adds up)
- **Runtime**: `DOTNETCORE|8.0`
- **Always On**: enabled (otherwise the bot misses incoming
  notifications during idle scale-down)
- **Web Sockets**: enabled (required for the backend ingest connection)
- **HTTP/2**: enabled
- **Managed Identity**: enable system-assigned, grant
  `Key Vault Secrets User` on `kv-calltranskript-prod` so the app
  can read `BotClientSecret` and `BackendIngestSecret`
- **App Settings**: use KV references for both secrets (see spec
  table)
- **Health check path**: `/health`

### Deployment artifact

Either:
- ZIP deploy via `az webapp deploy --src-path ...` (similar to how we
  deploy our Node.js backend)
- Container deploy via ACR

Both work; ZIP is simpler for a 2-3 week engagement.

### CI/CD

We can extend the existing `.github/workflows/ci.yml` to add a deploy
job for your service once you confirm where the code lives. Use
OIDC federated credentials to Azure (no stored secrets in GitHub).

---

## 9. Testing approach

| Test | How |
|---|---|
| Bot starts and listens on /api/calling | `curl https://your-bot/health` returns 200 |
| Graph notification validation | Run a test call, see the `IncomingCall` JSON in your bot logs |
| Bot joins call successfully | Bot log shows `Call established`, `Media session created` |
| Audio reaches our backend | Our `app-backend` logs show `=== BOT WS CONNECTED ===` followed by frame counts |
| Transcription appears in agent-ui | Open localhost:5173 with `acs-calltranskript@sgb-energie.de` agent; transcript scrolls during call |
| Concurrent calls (5 at once) | Place 5 calls; observe `bot_active_calls` metric ≤ 5; no errors |
| Bot leaves cleanly | Hang up; `call.ended` frame sent; WS closes; resources freed |
| Backend WS disconnect mid-call | Manually kill app-backend; bot logs WS reconnect attempts or graceful give-up |
| Auth failure (wrong bearer) | Set bad `Backend.IngestSecret` locally; backend rejects 401; bot retries/aborts |

---

## 10. Common pitfalls

1. **`Microsoft.Skype.Bots.Media` is Linux x64 only on .NET 8.** Won't
   work on Apple Silicon dev machines without Rosetta or Linux container.
2. **The Graph Communications JWT must be validated** on every incoming
   notification. Microsoft's sample includes this; don't strip it out.
3. **`ICall.Resource.MeetingInfo` is null for 1:1 PSTN calls** — your
   code must handle both meeting-style and 1:1 calls.
4. **Buffer ordering matters** — Send `call.started` text frame BEFORE
   any binary audio. Our backend buffers but logs a warning otherwise.
5. **Don't share `ClientWebSocket` across calls** — one per call,
   disposed on call.end. Concurrent calls each have their own.
6. **`HeartbeatHandler` is required** — the SDK times out calls that
   don't send heartbeats within ~10 min.
7. **Tenant ID, not common, for token auth** — your bot is single-tenant
   (`Bot.MsaAppType: SingleTenant` was set at registration).
8. **Calling permissions must be admin-consented** — we did this; don't
   re-trigger the consent flow from your code.

---

## 11. Telemetry

Emit these events with `correlationId` as a property:

```csharp
_telemetryClient.TrackEvent("bot_call_received", new Dictionary<string, string> {
    { "correlationId", _correlationId },
    { "callerPhone", callerPhone ?? "" },
});
```

| Event | When |
|---|---|
| `bot_call_received` | Graph notification arrives |
| `bot_call_joined` | Bot successfully joined |
| `bot_call_join_failed` | Join errored — include `errorCode` |
| `bot_backend_ws_connected` | Backend WS opened |
| `bot_backend_ws_failed` | Backend WS connect failed — include status |
| `bot_audio_first_frame_sent` | First binary frame sent to backend |
| `bot_call_left` | Bot exited call cleanly |
| `bot_call_error` | Mid-call error |

Metrics:

| Metric | What |
|---|---|
| `bot_join_latency_ms` | Notification arrival → bot joined |
| `bot_first_frame_latency_ms` | Bot joined → first audio frame sent |
| `bot_audio_forward_p95_ms` | Per-100-frames p95 of media-receive → ws-send |
| `bot_active_calls` | Gauge of concurrent calls |

---

## 12. Acceptance criteria

You're done when:

1. A real PSTN call to a Teams user with the policy assigned is
   automatically joined by your bot.
2. Audio from caller and agent reaches our Node.js backend within
   2 seconds of speech.
3. Our agent-ui shows live transcript + AI suggestions during the call.
4. 5 concurrent calls work without degradation.
5. Bot leaves cleanly on call end; no leaks.
6. Code + README + deploy script in private GitHub repo.
7. One operations runbook for common failure modes.
8. One hour of Q&A handover with us.

Budget: 2-3 weeks of focused work.
