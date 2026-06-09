using ComplianceRecordingBot.Authentication;
using ComplianceRecordingBot.Bot;
using ComplianceRecordingBot.Configuration;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// ── Kestrel port binding ─────────────────────────────────────────────────────
// Port source depends on hosting context:
//
//   Windows App Service outofprocess (IIS reverse proxy):
//     IIS sets ASPNETCORE_PORT to a random loopback port per process startup.
//     We must NOT pass a fixed port — read ASPNETCORE_PORT if present (set by IIS),
//     otherwise fall back to PORT / WEBSITES_PORT (Linux App Service) or 9442 (local dev).
//
//   Linux App Service (container):
//     Platform sets PORT (re-export of WEBSITES_PORT). Must listen on that port.
//
//   Local dev:
//     No PORT / ASPNETCORE_PORT — fall back to 9442.
var port = int.TryParse(
    Environment.GetEnvironmentVariable("ASPNETCORE_PORT") ??   // Windows IIS outofprocess
    Environment.GetEnvironmentVariable("PORT") ??              // Linux App Service container
    Environment.GetEnvironmentVariable("WEBSITES_PORT"),       // App Service setting (fallback)
    out var p) ? p : 9442;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

// ── Configuration ────────────────────────────────────────────────────────────
// All secrets come from environment variables / App Service settings.
// KV references (@Microsoft.KeyVault(...)) are resolved automatically by
// Azure App Service when managed identity has Key Vault Secrets User.
builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<BackendConfig>(builder.Configuration.GetSection("Backend"));

// ── Telemetry ────────────────────────────────────────────────────────────────
// AddApplicationInsightsTelemetry() reads the connection string from:
//   1. APPLICATIONINSIGHTS_CONNECTION_STRING env var (App Service setting name)
//   2. ApplicationInsights:ConnectionString in IConfiguration (local appsettings)
// We prefer the env var path which is what App Service sets via KV reference.
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString =
        Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")
        ?? builder.Configuration["ApplicationInsights:ConnectionString"];
});

// ── HTTP / Controllers ───────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Bot services ─────────────────────────────────────────────────────────────
//
// AuthenticationProvider: validates inbound Graph Communications JWTs and acquires
// outbound tokens. Scoped to the lifetime of the hosted service.
builder.Services.AddSingleton<AuthenticationProvider>();

// ComplianceRecordingBotService: builds ICommunicationsClient, registers per-call
// CallHandlers, and maintains the active call registry.
// Registered as both IHostedService (so ASP.NET starts/stops it) and as the concrete
// type (so PlatformCallController can inject it to access .Client).
builder.Services.AddSingleton<ComplianceRecordingBotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComplianceRecordingBotService>());

// ── Health checks ────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

// ── Build + pipeline ─────────────────────────────────────────────────────────
var app = builder.Build();

app.UseRouting();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
