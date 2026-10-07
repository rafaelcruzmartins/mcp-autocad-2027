using System.Text;

namespace AcadMcp.Infrastructure;

/// <summary>Append-only daily log file in %APPDATA%\AcadMcp\logs. Never throws.</summary>
internal static class Logger
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadMcp", "logs");

    public static string CurrentFile => Path.Combine(LogDirectory, $"acadmcp-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append("[T").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message)
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take AutoCAD down.
        }
    }
}
