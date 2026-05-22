namespace ComplianceRecordingBot.Configuration;

/// <summary>
/// Strongly-typed config for the "Bot" section in appsettings.json.
/// Populated from App Service environment variables via the standard .NET
/// configuration system (double-underscore as section separator, e.g. BOT__APPID).
/// </summary>
public class BotConfig
{
    /// <summary>Entra application (client) ID. Value: 7607addb-4830-4a98-be37-97ac0ebe3f8c</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>SGB Energie tenant ID. Value: d5663c64-53b6-427d-bd45-ad3d3b91764e</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Entra app client secret. Must be empty in source control.
    /// Sourced from KV reference: @Microsoft.KeyVault(VaultName=kv-calltranskript-prod;SecretName=BotClientSecret)
    /// </summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// The public hostname of this bot service (without scheme).
    /// e.g. bot-calltranskript-net.azurewebsites.net
    /// Used to build absolute URLs for Graph Communications registration.
    /// </summary>
    public string ServiceCname { get; set; } = string.Empty;

    /// <summary>
    /// Full URL of the /api/calling endpoint registered with Azure Bot Service.
    /// Must be HTTPS. Updated after every deploy if hostname changes.
    /// </summary>
    public string CallingWebHookEndpoint { get; set; } = string.Empty;

    /// <summary>Maximum concurrent calls this instance will handle. Default 20.</summary>
    public int MediaInstanceCapacity { get; set; } = 20;

    /// <summary>
    /// Certificate subject for the media service TLS certificate.
    /// Leave empty to use the default App Service cert.
    /// TODO (Session 2): configure once media workload is wired.
    /// </summary>
    public string MediaServiceCertSubject { get; set; } = string.Empty;
}
