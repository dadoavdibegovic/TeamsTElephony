namespace ComplianceRecordingBot.Configuration;

/// <summary>
/// Strongly-typed config for the "Backend" section in appsettings.json.
/// These settings describe the Node.js audio-ingest backend.
/// </summary>
public class BackendConfig
{
    /// <summary>
    /// Base WebSocket URL of the Node.js audio ingest endpoint.
    /// Per-call URL is: {IngestWss}/{correlationId}
    /// Default: wss://app-calltranskript-backend.azurewebsites.net/bot/audio
    /// </summary>
    public string IngestWss { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret sent as Bearer in the Authorization header during WS upgrade.
    /// Must be empty in source control.
    /// Sourced from KV reference: @Microsoft.KeyVault(VaultName=kv-calltranskript-prod;SecretName=BackendIngestSecret)
    /// </summary>
    public string IngestSecret { get; set; } = string.Empty;
}
