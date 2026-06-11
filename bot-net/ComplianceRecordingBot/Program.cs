using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using ComplianceRecordingBot.Authentication;
using ComplianceRecordingBot.Bot;
using ComplianceRecordingBot.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ── Key Vault configuration ──────────────────────────────────────────────────
// Non-secret config (AppId, TenantId, ServiceCname, endpoints) comes from
// appsettings[.Production].json. Secrets are pulled from Key Vault using the host's
// managed identity (the VM's system-assigned MI has Key Vault Secrets User), so no
// secret value is written to disk. KeyVault:Uri is set in appsettings.Production.json.
var kvUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(kvUri))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(kvUri),
        new DefaultAzureCredential(),
        new BotKeyVaultSecretManager());
}

// ── Kestrel TLS on 443 (self-hosted on the VM; no IIS) ───────────────────────
// The bot is hosted directly (Windows Service / console) on the VM, so Kestrel
// terminates TLS itself. The *.sgb-energie.de wildcard cert (loaded from KV as
// Bot:MediaCertPfxBase64) covers the bot FQDN call.sgb-energie.de.
// Guarded by cert presence so local dev (no KV/cert) keeps Kestrel defaults.
var pfxBase64 = builder.Configuration["Bot:MediaCertPfxBase64"];
var pfxPassword = builder.Configuration["Bot:MediaCertPfxPassword"];
if (!string.IsNullOrWhiteSpace(pfxBase64))
{
    var tlsCert = new X509Certificate2(
        Convert.FromBase64String(pfxBase64),
        pfxPassword,
        X509KeyStorageFlags.MachineKeySet);
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(443, listen => listen.UseHttps(tlsCert));
    });
}

// ── Windows Service hosting ──────────────────────────────────────────────────
// Lets the published exe run under the Windows Service Control Manager on the VM.
// Harmless when launched as a plain console process (e.g. local dev).
builder.Host.UseWindowsService();

// ── Configuration ────────────────────────────────────────────────────────────
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
