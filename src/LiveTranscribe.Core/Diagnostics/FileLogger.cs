using System.Text;
using System.Text.RegularExpressions;

namespace LiveTranscribe.Core.Diagnostics;

/// <summary>Minimal rolling file log. Redaction is a safety net, not a licence to log secrets (FR-11.5).</summary>
public sealed partial class FileLogger : IAppLogger
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileLogger(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Info(string message) => Write("INF", message);

    public void Warn(string message) => Write("WRN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message)}{Environment.NewLine}";
        var path = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");

        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break dictation.
            }
        }
    }

    internal static string Redact(string message) => ApiKeyPattern().Replace(message, "<redacted-key>");

    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{10,}")]
    private static partial Regex ApiKeyPattern();
}
