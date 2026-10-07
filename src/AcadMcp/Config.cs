using System.Text.Json;
using AcadMcp.Infrastructure;

namespace AcadMcp;

/// <summary>
/// Settings from %APPDATA%\AcadMcp\config.json (created with defaults on first run).
/// The ACADMCP_PORT environment variable overrides the port.
/// </summary>
internal sealed class Config
{
    public const int DefaultPort = 5100;

    public int Port { get; set; } = DefaultPort;
    public bool AutoStart { get; set; } = true;
    /// <summary>Seconds a tool call may wait for the AutoCAD main thread before failing.</summary>
    public int CallTimeoutSeconds { get; set; } = 120;

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadMcp", "config.json");

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Config Current { get; private set; } = new();

    public static Config Load()
    {
        var cfg = new Config();
        try
        {
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), FileJson) ?? new Config();
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, FileJson));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Falha ao ler {FilePath}; usando padrões.", ex);
            cfg = new Config();
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("ACADMCP_PORT"), out var envPort))
            cfg.Port = envPort;

        if (cfg.Port is < 1024 or > 65535)
        {
            Logger.Warn($"Porta inválida {cfg.Port}; usando {DefaultPort}.");
            cfg.Port = DefaultPort;
        }
        if (cfg.Port is >= 5001 and <= 5050)
            Logger.Warn($"Porta {cfg.Port} está na faixa 5001-5050 usada pelo MCP oficial da Autodesk.");
        if (cfg.CallTimeoutSeconds <= 0) cfg.CallTimeoutSeconds = 120;

        Current = cfg;
        return cfg;
    }
}
