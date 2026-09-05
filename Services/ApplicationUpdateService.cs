using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IsraeliAuthorStudio.Services;

public enum ApplicationUpdateState
{
    Idle,
    Checking,
    Downloading,
    Ready,
    Installing,
    Failed
}

public sealed record ApplicationUpdateSnapshot(
    ApplicationUpdateState State,
    string? Version = null,
    string? ReleaseUrl = null,
    string? Error = null);

public sealed record ApplicationRelease(
    Version Version,
    string VersionText,
    Uri ReleaseUrl,
    Uri PackageUrl,
    string PackageName,
    long PackageSize,
    Uri ChecksumsUrl);

public sealed class ApplicationUpdateOptions
{
    public bool Enabled { get; init; }
    public Version CurrentVersion { get; init; } = ApplicationUpdateManifest.GetCurrentVersion();
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);
}

public sealed class ApplicationUpdateService(
    IHttpClientFactory httpClientFactory,
    ApplicationDataPaths dataPaths,
    ApplicationUpdateOptions options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ApplicationUpdateService> logger) : BackgroundService
{
    private const long MaximumPackageSize = 300L * 1024 * 1024;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateLock = new();
    private ApplicationUpdateSnapshot _snapshot = new(ApplicationUpdateState.Idle);
    private string? _downloadedPackagePath;
    private string? _blockedVersion;

    public event Action? StateChanged;

    public bool IsSupported => options.Enabled;

    public ApplicationUpdateSnapshot Snapshot
    {
        get
        {
            lock (_stateLock) return _snapshot;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsSupported) return;

        try
        {
            LoadPreviousUpdateResult();
            await Task.Delay(options.InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAndDownloadAsync(reportConnectionFailure: false, stoppingToken);
                await Task.Delay(options.CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        _blockedVersion = null;
        TryDelete(Path.Combine(dataPaths.RootPath, "Updates", "last-update-result.txt"));
        return CheckAndDownloadAsync(reportConnectionFailure: true, cancellationToken);
    }

    public async Task CheckAndDownloadAsync(
        bool reportConnectionFailure = true,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return;
        var releaseDiscovered = false;
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.State is ApplicationUpdateState.Ready or ApplicationUpdateState.Installing) return;
            SetSnapshot(new(ApplicationUpdateState.Checking));
            var client = httpClientFactory.CreateClient("ApplicationUpdates");
            using var response = await client.GetAsync(
                "repos/shachar-roth/BookWriter/releases/latest",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                SetSnapshot(new(ApplicationUpdateState.Idle));
                return;
            }

            response.EnsureSuccessStatusCode();
            await using var releaseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await ApplicationUpdateManifest.ParseAsync(
                releaseStream,
                RuntimeInformation.ProcessArchitecture,
                cancellationToken);
            if (release is null || release.Version <= options.CurrentVersion)
            {
                SetSnapshot(new(ApplicationUpdateState.Idle));
                return;
            }

            releaseDiscovered = true;
            if (string.Equals(release.VersionText, _blockedVersion, StringComparison.Ordinal))
            {
                SetSnapshot(new(
                    ApplicationUpdateState.Failed,
                    release.VersionText,
                    release.ReleaseUrl.ToString(),
                    "העדכון הקודם נכשל והגרסה הקודמת שוחזרה."));
                return;
            }

            SetSnapshot(new(ApplicationUpdateState.Downloading, release.VersionText, release.ReleaseUrl.ToString()));
            var updateDirectory = Path.Combine(dataPaths.RootPath, "Updates", release.VersionText);
            Directory.CreateDirectory(updateDirectory);
            var packagePath = Path.Combine(updateDirectory, release.PackageName);
            var partialPath = $"{packagePath}.partial";
            TryDelete(partialPath);

            var checksums = await DownloadTextAsync(client, release.ChecksumsUrl, 64 * 1024, cancellationToken);
            await DownloadFileAsync(client, release.PackageUrl, partialPath, MaximumPackageSize, cancellationToken);
            await ApplicationUpdatePackageVerifier.VerifyAsync(
                partialPath,
                release.PackageName,
                checksums,
                release.VersionText,
                cancellationToken);
            File.Move(partialPath, packagePath, overwrite: true);

            _downloadedPackagePath = packagePath;
            SetSnapshot(new(ApplicationUpdateState.Ready, release.VersionText, release.ReleaseUrl.ToString()));
            logger.LogInformation("Application update {Version} is downloaded and verified.", release.VersionText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Application update check or download failed.");
            SetSnapshot(reportConnectionFailure || releaseDiscovered
                ? new(ApplicationUpdateState.Failed, Error: "לא ניתן לבדוק או להוריד את העדכון כרגע.")
                : new(ApplicationUpdateState.Idle));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return;
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot;
            if (snapshot.State != ApplicationUpdateState.Ready ||
                string.IsNullOrWhiteSpace(snapshot.Version) ||
                string.IsNullOrWhiteSpace(_downloadedPackagePath) ||
                !File.Exists(_downloadedPackagePath))
            {
                return;
            }

            var appPath = MacApplicationUpdateInstaller.ResolveCurrentAppBundle(AppContext.BaseDirectory);
            if (appPath is null)
            {
                throw new InvalidOperationException("The current macOS application bundle could not be located.");
            }

            var updateRoot = Path.Combine(dataPaths.RootPath, "Updates");
            Directory.CreateDirectory(updateRoot);
            var helperPath = Path.Combine(updateRoot, $"install-{Guid.NewGuid():N}.sh");
            var healthPath = Path.Combine(updateRoot, $"health-{Guid.NewGuid():N}.json");
            var resultPath = Path.Combine(updateRoot, "last-update-result.txt");
            var logPath = Path.Combine(dataPaths.RootPath, "Logs", "updater.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var helperScript = MacApplicationUpdateInstaller.HelperScript.Replace("\r\n", "\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(helperPath, helperScript, new System.Text.UTF8Encoding(false), cancellationToken);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    helperPath,
                    appPath,
                    _downloadedPackagePath,
                    snapshot.Version,
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    healthPath,
                    resultPath,
                    logPath
                }
            });
            if (process is null) throw new InvalidOperationException("The update helper could not be started.");

            SetSnapshot(snapshot with { State = ApplicationUpdateState.Installing });
            logger.LogInformation("Application update {Version} installation started.", snapshot.Version);
            await Task.Delay(300, cancellationToken);
            applicationLifetime.StopApplication();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Application update installation could not start.");
            SetSnapshot(new(ApplicationUpdateState.Failed, Error: "לא ניתן להתחיל את התקנת העדכון."));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void SetSnapshot(ApplicationUpdateSnapshot snapshot)
    {
        lock (_stateLock) _snapshot = snapshot;
        StateChanged?.Invoke();
    }

    private void LoadPreviousUpdateResult()
    {
        var resultPath = Path.Combine(dataPaths.RootPath, "Updates", "last-update-result.txt");
        try
        {
            if (!File.Exists(resultPath)) return;
            var result = File.ReadAllText(resultPath).Trim();
            if (result.StartsWith("success:", StringComparison.Ordinal))
            {
                logger.LogInformation("{UpdateResult}", result);
                File.Delete(resultPath);
                return;
            }

            var parts = result.Split(':', 3);
            if (parts.Length >= 2 && parts[0] is "failed" or "rolled-back")
            {
                _blockedVersion = parts[1];
                logger.LogWarning("Previous application update result: {UpdateResult}", result);
                SetSnapshot(new(
                    ApplicationUpdateState.Failed,
                    _blockedVersion,
                    Error: parts[0] == "rolled-back"
                        ? "העדכון נכשל והגרסה הקודמת שוחזרה באופן אוטומטי."
                        : "התקנת העדכון נכשלה. היישום הקיים נשאר ללא שינוי."));
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Previous application update result could not be read.");
        }
    }

    private static async Task<string> DownloadTextAsync(
        HttpClient client,
        Uri address,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateReleaseAssetUrl(address);
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Checksum file is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        await CopyWithLimitAsync(stream, memory, maximumBytes, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        Uri address,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateReleaseAssetUrl(address);
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Update package is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyWithLimitAsync(input, output, maximumBytes, cancellationToken);
    }

    private static async Task CopyWithLimitAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("Downloaded content exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateReleaseAssetUrl(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(address.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !address.AbsolutePath.StartsWith("/shachar-roth/BookWriter/releases/download/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Release asset URL is not trusted.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public static class ApplicationUpdateManifest
{
    private const string RepositoryReleasePrefix = "https://github.com/shachar-roth/BookWriter/releases/";

    public static Version GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public static async Task<ApplicationRelease?> ParseAsync(
        Stream json,
        Architecture architecture,
        CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision >= 0)
        {
            return null;
        }

        var versionText = $"{version.Major}.{version.Minor}.{version.Build}";
        var releaseUrlText = root.GetProperty("html_url").GetString();
        if (!Uri.TryCreate(releaseUrlText, UriKind.Absolute, out var releaseUrl) ||
            !releaseUrl.ToString().StartsWith(RepositoryReleasePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var architectureName = architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => null
        };
        if (architectureName is null) return null;

        var packageName = $"IsraeliAuthorStudio-macos-{architectureName}.zip";
        JsonElement? package = null;
        JsonElement? checksums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, packageName, StringComparison.Ordinal)) package = asset;
            if (string.Equals(name, "SHA256SUMS.txt", StringComparison.Ordinal)) checksums = asset;
        }
        if (package is null || checksums is null) return null;

        var packageUrl = ReadTrustedAssetUrl(package.Value);
        var checksumsUrl = ReadTrustedAssetUrl(checksums.Value);
        var packageSize = package.Value.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
        if (packageUrl is null || checksumsUrl is null || packageSize is <= 0 or > 300L * 1024 * 1024) return null;

        return new ApplicationRelease(version, versionText, releaseUrl, packageUrl, packageName, packageSize, checksumsUrl);
    }

    private static Uri? ReadTrustedAssetUrl(JsonElement asset)
    {
        var value = asset.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/shachar-roth/BookWriter/releases/download/", StringComparison.Ordinal))
        {
            return null;
        }
        return uri;
    }
}

public static class ApplicationUpdatePackageVerifier
{
    private static readonly Regex ChecksumLine = new(
        "^([a-fA-F0-9]{64})\\s+\\*?(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task VerifyAsync(
        string packagePath,
        string packageName,
        string checksums,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var expectedHash = checksums
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ChecksumLine.Match(line))
            .Where(match => match.Success && string.Equals(Path.GetFileName(match.Groups[2].Value), packageName, StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .SingleOrDefault();
        if (expectedHash is null) throw new InvalidDataException("The update checksum is missing or duplicated.");

        await using (var stream = File.OpenRead(packagePath))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(expectedHash)))
            {
                throw new InvalidDataException("The update package checksum does not match.");
            }
        }

        using var archive = ZipFile.OpenRead(packagePath);
        const string bundleRoot = "Israeli Author Studio.app/";
        const string executablePath = bundleRoot + "Contents/MacOS/IsraeliAuthorStudio";
        const string infoPath = bundleRoot + "Contents/Info.plist";
        var totalExpandedSize = 0L;
        ZipArchiveEntry? infoEntry = null;
        var hasExecutable = false;
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.StartsWith('/') || segments.Any(segment => segment is "." or "..") ||
                !path.StartsWith(bundleRoot, StringComparison.Ordinal) ||
                !normalizedPaths.Add(path))
            {
                throw new InvalidDataException("The update package contains an unsafe path.");
            }

            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000) throw new InvalidDataException("The update package contains a symbolic link.");

            totalExpandedSize += entry.Length;
            if (totalExpandedSize > 1024L * 1024 * 1024) throw new InvalidDataException("The expanded update is too large.");
            if (string.Equals(path, executablePath, StringComparison.Ordinal)) hasExecutable = true;
            if (string.Equals(path, infoPath, StringComparison.Ordinal)) infoEntry = entry;
        }

        if (!hasExecutable || infoEntry is null) throw new InvalidDataException("The update package is not a valid application bundle.");
        await using var infoStream = infoEntry.Open();
        var plist = await XDocument.LoadAsync(infoStream, LoadOptions.None, cancellationToken);
        var versionKey = plist.Descendants("key")
            .FirstOrDefault(element => string.Equals(element.Value, "CFBundleShortVersionString", StringComparison.Ordinal));
        var bundleVersion = versionKey?.ElementsAfterSelf().FirstOrDefault()?.Value;
        if (!string.Equals(bundleVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The application bundle version does not match the release.");
        }
    }
}

public static class MacApplicationUpdateInstaller
{
    public static string? ResolveCurrentAppBundle(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        while (directory is not null)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    public const string HelperScript = """
        #!/bin/sh
        set -u

        APP_PATH="$1"
        PACKAGE_PATH="$2"
        EXPECTED_VERSION="$3"
        OLD_PID="$4"
        HEALTH_PATH="$5"
        RESULT_PATH="$6"
        LOG_PATH="$7"
        APP_NAME="$(basename "$APP_PATH")"
        STAGE_ROOT="${APP_PATH}.update-$$"
        BACKUP_PATH="${APP_PATH}.previous"
        NEW_APP="$STAGE_ROOT/$APP_NAME"
        NEW_EXECUTABLE="$APP_PATH/Contents/MacOS/IsraeliAuthorStudio"

        exec >>"$LOG_PATH" 2>&1
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) Starting update to $EXPECTED_VERSION"

        write_result() {
          printf '%s\n' "$1" >"$RESULT_PATH"
        }

        relaunch_current() {
          if [ -d "$APP_PATH" ]; then
            /usr/bin/open -n "$APP_PATH" >/dev/null 2>&1 || true
          fi
        }

        fail_before_swap() {
          echo "$1"
          write_result "failed:$EXPECTED_VERSION:$1"
          rm -rf "$STAGE_ROOT"
          relaunch_current
          exit 1
        }

        rm -rf "$STAGE_ROOT"
        mkdir -p "$STAGE_ROOT" || fail_before_swap "Could not create the update staging directory."
        /usr/bin/ditto -x -k "$PACKAGE_PATH" "$STAGE_ROOT" || fail_before_swap "Could not extract the update package."
        [ -x "$NEW_APP/Contents/MacOS/IsraeliAuthorStudio" ] || fail_before_swap "The downloaded application is incomplete."
        ACTUAL_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$NEW_APP/Contents/Info.plist" 2>/dev/null)"
        [ "$ACTUAL_VERSION" = "$EXPECTED_VERSION" ] || fail_before_swap "The downloaded application version is incorrect."

        ATTEMPT=0
        while kill -0 "$OLD_PID" 2>/dev/null && [ "$ATTEMPT" -lt 300 ]; do
          sleep 0.1
          ATTEMPT=$((ATTEMPT + 1))
        done
        if kill -0 "$OLD_PID" 2>/dev/null; then
          fail_before_swap "The previous application process did not close."
        fi

        rm -f "$HEALTH_PATH"
        rm -rf "$BACKUP_PATH"
        mv "$APP_PATH" "$BACKUP_PATH" || fail_before_swap "The application folder is not writable."
        if ! mv "$NEW_APP" "$APP_PATH"; then
          mv "$BACKUP_PATH" "$APP_PATH" || true
          fail_before_swap "Could not place the updated application."
        fi
        rm -rf "$STAGE_ROOT"

        nohup "$NEW_EXECUTABLE" "--update-health-file=$HEALTH_PATH" >/dev/null 2>&1 &
        NEW_PID=$!
        ATTEMPT=0
        while [ ! -f "$HEALTH_PATH" ] && kill -0 "$NEW_PID" 2>/dev/null && [ "$ATTEMPT" -lt 600 ]; do
          sleep 0.1
          ATTEMPT=$((ATTEMPT + 1))
        done

        if [ -f "$HEALTH_PATH" ]; then
          write_result "success:$EXPECTED_VERSION"
          rm -rf "$BACKUP_PATH"
          rm -f "$PACKAGE_PATH" "$HEALTH_PATH" "$0"
          echo "Update completed successfully."
          exit 0
        fi

        echo "Updated application failed its startup health check; rolling back."
        kill "$NEW_PID" 2>/dev/null || true
        ATTEMPT=0
        while kill -0 "$NEW_PID" 2>/dev/null && [ "$ATTEMPT" -lt 100 ]; do
          sleep 0.1
          ATTEMPT=$((ATTEMPT + 1))
        done
        rm -rf "$APP_PATH"
        if mv "$BACKUP_PATH" "$APP_PATH"; then
          write_result "rolled-back:$EXPECTED_VERSION"
          relaunch_current
        else
          write_result "failed:$EXPECTED_VERSION:Automatic rollback failed. The backup remains at $BACKUP_PATH"
        fi
        rm -rf "$STAGE_ROOT"
        exit 1
        """;
}
