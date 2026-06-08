using ComplianceRecordingBot.Authentication;
using ComplianceRecordingBot.Bot;
using ComplianceRecordingBot.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
// All secrets come from environment variables / App Service settings.
// KV references (@Microsoft.KeyVault(...)) are resolved automatically by
// Azure App Service when managed identity has Key Vault Secrets User.
builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<BackendConfig>(builder.Configuration.GetSection("Backend"));

// ── Telemetry ────────────────────────────────────────────────────────────────
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
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
