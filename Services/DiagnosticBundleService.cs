using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace IsraeliAuthorStudio.Services;

public sealed class DiagnosticBundleService
{
    private const long MaximumFileBytes = 100 * 1024 * 1024;
    private const long MaximumBundleInputBytes = 250 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ApplicationDataPaths _applicationData;
    private readonly ProjectSelectionService _projects;
    private readonly ILogger<DiagnosticBundleService> _logger;

    public DiagnosticBundleService(
        ApplicationDataPaths applicationData,
        ProjectSelectionService projects,
        ILogger<DiagnosticBundleService> logger)
    {
        _applicationData = applicationData;
        _projects = projects;
        _logger = logger;
    }

    public async Task<DiagnosticBundle> CreateAsync(CancellationToken cancellationToken = default)
    {
        var projectRoot = Path.GetFullPath(_projects.CurrentProjectPath);
        if (!Directory.Exists(projectRoot)) throw new DirectoryNotFoundException("The current project folder does not exist.");

        var omitted = new List<string>();
        var includedFiles = 0;
        long includedBytes = 0;
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in EnumerateProjectFiles(projectRoot, omitted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
                var length = new FileInfo(path).Length;
                if (length > MaximumFileBytes || includedBytes + length > MaximumBundleInputBytes)
                {
                    omitted.Add($"project/{relativePath} (size limit)");
                    continue;
                }

                if (await TryAddFileAsync(archive, path, $"project/{relativePath}", cancellationToken))
                {
                    includedFiles++;
                    includedBytes += length;
                }
                else
                {
                    omitted.Add($"project/{relativePath} (unreadable)");
                }
            }

            var logsRoot = Path.Combine(_applicationData.RootPath, "Logs");
            if (Directory.Exists(logsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(logsRoot, "studio-*.log")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Take(14))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = $"diagnostics/logs/{Path.GetFileName(path)}";
                    if (!await TryAddFileAsync(archive, path, entryName, cancellationToken)) omitted.Add($"{entryName} (unreadable)");
                }
            }

            var manifest = new DiagnosticManifest
            {
                CreatedAt = DateTimeOffset.UtcNow,
                ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                ProjectName = new DirectoryInfo(projectRoot).Name,
                IncludedProjectFiles = includedFiles,
                IncludedProjectBytes = includedBytes,
                Omitted = omitted,
                Notes = "The bundle contains the manuscript. API credentials and Git internals are intentionally excluded."
            };
            await WriteTextEntryAsync(
                archive,
                "diagnostics/manifest.json",
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
        }

        _logger.LogInformation(
            "Created diagnostic bundle for project {ProjectName} with {FileCount} project files and {ByteCount} input bytes.",
            new DirectoryInfo(projectRoot).Name,
            includedFiles,
            includedBytes);
        var safeProjectName = SanitizeFileName(new DirectoryInfo(projectRoot).Name);
        return new DiagnosticBundle(
            $"IsraeliAuthorStudio-diagnostics-{safeProjectName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
            output.ToArray());
    }

    private static IEnumerable<string> EnumerateProjectFiles(string projectRoot, List<string> omitted)
    {
        var pending = new Stack<string>();
        pending.Push(projectRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch
            {
                omitted.Add($"project/{Path.GetRelativePath(projectRoot, directory).Replace('\\', '/')} (unreadable directory)");
                continue;
            }

            foreach (var path in children)
            {
                var relativePath = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch
                {
                    omitted.Add($"project/{relativePath} (unreadable attributes)");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    omitted.Add($"project/{relativePath} (symbolic link)");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsExcludedDirectory(relativePath)) omitted.Add($"project/{relativePath}/ (excluded)");
                    else pending.Push(path);
                    continue;
                }

                if (IsExcludedFile(relativePath)) omitted.Add($"project/{relativePath} (excluded)");
                else yield return path;
            }
        }
    }

    private static bool IsExcludedDirectory(string relativePath) =>
        relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals(".history", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals(".studio/cache", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.Equals("assistant-settings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("current-project.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(".tmp-", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryAddFileAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var output = entry.Open();
            await input.CopyToAsync(output, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "project" : sanitized;
    }

    private sealed class DiagnosticManifest
    {
        public DateTimeOffset CreatedAt { get; set; }
        public string ApplicationVersion { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Framework { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public int IncludedProjectFiles { get; set; }
        public long IncludedProjectBytes { get; set; }
        public List<string> Omitted { get; set; } = [];
        public string Notes { get; set; } = "";
    }
}

public sealed record DiagnosticBundle(string FileName, byte[] Content);

public sealed class ClientDiagnosticEvent
{
    public string Level { get; set; } = "error";
    public string Message { get; set; } = "";
    public string Stack { get; set; } = "";
    public string Page { get; set; } = "";
    public string Browser { get; set; } = "";
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
}
