using System.Diagnostics;

namespace IsraeliAuthorStudio.Services;

public static class DesktopApplication
{
    private const string EndpointFileName = "desktop-endpoint.txt";

    public static bool IsDesktopLaunch(string[] args)
    {
        if (args.Contains("--desktop", StringComparer.OrdinalIgnoreCase)) return true;
        if (!OperatingSystem.IsMacOS()) return false;

        var baseDirectory = AppContext.BaseDirectory.Replace('\\', '/');
        return baseDirectory.Contains(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase);
    }

    public static DesktopSession? AcquireOrOpenExisting(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var lockPath = Path.Combine(dataRoot, "desktop.lock");
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            var endpointPath = Path.Combine(dataRoot, EndpointFileName);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (TryReadEndpoint(endpointPath, out var endpoint))
                {
                    OpenBrowser(endpoint);
                    return null;
                }
                Thread.Sleep(100);
            }

            ShowError("Israeli Author Studio is already running, but its browser address is not available yet.");
            return null;
        }

        var endpointFile = Path.Combine(dataRoot, EndpointFileName);
        TryDelete(endpointFile);
        return new DesktopSession(lockStream, endpointFile);
    }

    public static void OpenBrowser(string baseAddress)
    {
        var address = $"{baseAddress.TrimEnd('/')}/";
        if (OperatingSystem.IsMacOS())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { address }
            });
            return;
        }

        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    private static bool TryReadEndpoint(string path, out string endpoint)
    {
        endpoint = "";
        try
        {
            if (!File.Exists(path)) return false;
            var value = File.ReadAllText(path).Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return false;
            endpoint = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowError(string message)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var escaped = message.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-e", $"display alert \"Israeli Author Studio\" message \"{escaped}\"" }
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public sealed class DesktopSession : IDisposable
{
    private readonly FileStream _lockStream;
    private readonly string _endpointPath;

    internal DesktopSession(FileStream lockStream, string endpointPath)
    {
        _lockStream = lockStream;
        _endpointPath = endpointPath;
    }

    public void PublishEndpoint(string endpoint)
    {
        var temporaryPath = $"{_endpointPath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temporaryPath, endpoint);
        File.Move(temporaryPath, _endpointPath, overwrite: true);
    }

    public void Dispose()
    {
        TryDeleteEndpoint();
        _lockStream.Dispose();
    }

    private void TryDeleteEndpoint()
    {
        try { if (File.Exists(_endpointPath)) File.Delete(_endpointPath); }
        catch { }
    }
}
