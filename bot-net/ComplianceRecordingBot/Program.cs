using ComplianceRecordingBot.Configuration;
using Microsoft.ApplicationInsights.Extensibility;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
// All secrets come from environment variables / App Service settings.
// Never hardcode secrets here or in appsettings.json.
// KV references (e.g. @Microsoft.KeyVault(VaultName=...;SecretName=...))
// are resolved automatically by Azure App Service when managed identity is
// granted Key Vault Secrets User.

builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<BackendConfig>(builder.Configuration.GetSection("Backend"));

// ── Telemetry ────────────────────────────────────────────────────────────────
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
});

// ── HTTP / Controllers ───────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Bot services (stubs — Graph Communications wiring comes next session) ────
// TODO (Session 2): Register ICommunicationsClient, IGraphLogger, CallHandlerFactory
// TODO (Session 2): Register BackendWebSocketClientFactory

// ── Health checks ────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseRouting();

// ── Endpoints ────────────────────────────────────────────────────────────────
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
