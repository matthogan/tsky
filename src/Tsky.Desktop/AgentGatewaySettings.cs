using System.IO;
using System.Text.Json;

namespace tsky;

public sealed class AgentGatewaySettings
{
    public bool EnableTlsListener { get; set; }

    public string BindHost { get; set; } = "localhost";

    public int Port { get; set; } = AgentGatewayHost.DefaultPort;

    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public bool IsDevLoopbackFallbackEnabled { get; set; } = true;
}

public sealed class AgentGatewaySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky",
        "agents",
        "gateway-settings.json");

    public AgentGatewaySettings Load()
    {
        var settings = JsonFileStorage.LoadOrDefault<AgentGatewaySettings>(SettingsFilePath, JsonOptions);
        settings.BindHost = string.IsNullOrWhiteSpace(settings.BindHost) ? "localhost" : settings.BindHost.Trim();
        settings.Port = settings.Port <= 0 ? AgentGatewayHost.DefaultPort : settings.Port;
        return settings;
    }

    public void Save(AgentGatewaySettings settings)
    {
        settings.BindHost = string.IsNullOrWhiteSpace(settings.BindHost) ? "localhost" : settings.BindHost.Trim();
        settings.Port = settings.Port <= 0 ? AgentGatewayHost.DefaultPort : settings.Port;
        JsonFileStorage.Save(SettingsFilePath, settings, JsonOptions);
    }
}
