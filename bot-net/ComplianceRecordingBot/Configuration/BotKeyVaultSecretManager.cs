using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace ComplianceRecordingBot.Configuration;

/// <summary>
/// Maps the handful of flat Key Vault secret names this bot needs onto the structured
/// configuration keys the app reads. On the VM the secrets are pulled from Key Vault via
/// the VM's system-assigned managed identity (Key Vault Secrets User), so no secret value
/// is ever written to disk or baked into the deployment.
///
/// On App Service this mapping was done by individual app settings whose values were
/// @Microsoft.KeyVault(...) references (e.g. Bot__AppSecret -> SecretName=BotClientSecret).
/// KV secret names can't contain ':' or '_', so we translate explicitly here instead.
///
/// Only the secrets listed in <see cref="Map"/> are loaded; everything else in the vault
/// (OpenAI keys, CRM keys, etc.) is ignored.
/// </summary>
public sealed class BotKeyVaultSecretManager : KeyVaultSecretManager
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BotClientSecret"]            = "Bot:AppSecret",
        ["BotMediaCertPfx"]            = "Bot:MediaCertPfxBase64",
        ["BotMediaCertPfxPassword"]    = "Bot:MediaCertPfxPassword",
        ["BackendIngestSecret"]        = "Backend:IngestSecret",
        ["AppInsightsConnectionString"] = "ApplicationInsights:ConnectionString",
    };

    public override bool Load(SecretProperties properties) => Map.ContainsKey(properties.Name);

    public override string GetKey(KeyVaultSecret secret) => Map[secret.Name];
}
