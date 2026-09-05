using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IsraeliAuthorStudio.Services;

public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private const long MaximumLogFileBytes = 5 * 1024 * 1024;
    private static readonly Regex ApiKeyPattern = new(@"\bsk-[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled);
    private static readonly Regex BearerPattern = new(@"(?i)(Authorization\s*:\s*Bearer\s+)\S+", RegexOptions.Compiled);
    private static readonly Regex CredentialUrlPattern = new(@"(?i)(https?://)[^\s/:@]+:[^\s/@]+@", RegexOptions.Compiled);
    private static readonly Regex JsonSecretPattern = new("""(?i)("(?:apiKey|accessToken|password|secret)"\s*:\s*")[^"]+""", RegexOptions.Compiled);
    private readonly object _writeGate = new();
    private readonly string _logsRoot;

    public LocalFileLoggerProvider(ApplicationDataPaths applicationData)
    {
        _logsRoot = Path.Combine(applicationData.RootPath, "Logs");
        Directory.CreateDirectory(_logsRoot);
        DeleteExpiredLogs();
    }

    public ILogger CreateLogger(string categoryName) => new LocalFileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (level < LogLevel.Information) return;

        var builder = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(category);
        if (eventId.Id != 0) builder.Append(" (").Append(eventId.Id).Append(')');
        builder.Append(": ").Append(Redact(message));
        if (exception is not null) builder.AppendLine().Append(Redact(exception.ToString()));
        builder.AppendLine();

        lock (_writeGate)
        {
            try
            {
                File.AppendAllText(GetCurrentLogPath(), builder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never terminate the application.
            }
        }
    }

    internal static string Redact(string value)
    {
        var redacted = ApiKeyPattern.Replace(value ?? "", "[REDACTED_API_KEY]");
        redacted = BearerPattern.Replace(redacted, "$1[REDACTED]");
        redacted = CredentialUrlPattern.Replace(redacted, "$1[REDACTED]@");
        return JsonSecretPattern.Replace(redacted, "$1[REDACTED]");
    }

    private string GetCurrentLogPath()
    {
        var prefix = $"studio-{DateTimeOffset.Now:yyyyMMdd}";
        for (var sequence = 0; sequence < 100; sequence++)
        {
            var suffix = sequence == 0 ? "" : $"-{sequence}";
            var path = Path.Combine(_logsRoot, $"{prefix}{suffix}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogFileBytes) return path;
        }

        return Path.Combine(_logsRoot, $"{prefix}-overflow.log");
    }

    private void DeleteExpiredLogs()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_logsRoot, "studio-*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-14)) File.Delete(path);
            }
        }
        catch
        {
            // Retention cleanup is best effort.
        }
    }

    private sealed class LocalFileLogger(LocalFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(logLevel, category, eventId, formatter(state, exception), exception);
        }
    }
}
