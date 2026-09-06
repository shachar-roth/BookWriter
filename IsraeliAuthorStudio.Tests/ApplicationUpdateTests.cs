using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using IsraeliAuthorStudio.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IsraeliAuthorStudio.Tests;

public sealed class ApplicationUpdateTests
{
    [Theory]
    [InlineData(Architecture.Arm64, "IsraeliAuthorStudio-macos-arm64.zip")]
    [InlineData(Architecture.X64, "IsraeliAuthorStudio-macos-x64.zip")]
    public async Task ManifestSelectsPackageForCurrentArchitecture(Architecture architecture, string expectedName)
    {
        var json = $$"""
            {
              "tag_name": "v1.4.2",
              "html_url": "https://github.com/shachar-roth/BookWriter/releases/tag/v1.4.2",
              "assets": [
                {
                  "name": "IsraeliAuthorStudio-macos-arm64.zip",
                  "size": 100,
                  "browser_download_url": "https://github.com/shachar-roth/BookWriter/releases/download/v1.4.2/IsraeliAuthorStudio-macos-arm64.zip"
                },
                {
                  "name": "IsraeliAuthorStudio-macos-x64.zip",
                  "size": 100,
                  "browser_download_url": "https://github.com/shachar-roth/BookWriter/releases/download/v1.4.2/IsraeliAuthorStudio-macos-x64.zip"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "size": 100,
                  "browser_download_url": "https://github.com/shachar-roth/BookWriter/releases/download/v1.4.2/SHA256SUMS.txt"
                }
              ]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var release = await ApplicationUpdateManifest.ParseAsync(stream, architecture);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 4, 2), release.Version);
        Assert.Equal(expectedName, release.PackageName);
    }

    [Fact]
    public async Task ManifestRejectsAssetFromAnotherRepository()
    {
        const string json = """
            {
              "tag_name": "v2.0.0",
              "html_url": "https://github.com/shachar-roth/BookWriter/releases/tag/v2.0.0",
              "assets": [
                {
                  "name": "IsraeliAuthorStudio-macos-arm64.zip",
                  "size": 100,
                  "browser_download_url": "https://github.com/another/repository/releases/download/v2.0.0/IsraeliAuthorStudio-macos-arm64.zip"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "size": 100,
                  "browser_download_url": "https://github.com/shachar-roth/BookWriter/releases/download/v2.0.0/SHA256SUMS.txt"
                }
              ]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Null(await ApplicationUpdateManifest.ParseAsync(stream, Architecture.Arm64));
    }

    [Fact]
    public async Task PackageVerifierAcceptsMatchingChecksumAndBundleVersion()
    {
        using var directory = new TemporaryDirectory();
        const string packageName = "IsraeliAuthorStudio-macos-arm64.zip";
        var packagePath = Path.Combine(directory.Path, packageName);
        CreatePackage(packagePath, "1.5.0");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))).ToLowerInvariant();

        await ApplicationUpdatePackageVerifier.VerifyAsync(packagePath, packageName, $"{hash}  {packageName}", "1.5.0");
    }

    [Fact]
    public async Task PackageVerifierRejectsMismatchedBundleVersion()
    {
        using var directory = new TemporaryDirectory();
        const string packageName = "IsraeliAuthorStudio-macos-arm64.zip";
        var packagePath = Path.Combine(directory.Path, packageName);
        CreatePackage(packagePath, "1.4.9");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))).ToLowerInvariant();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ApplicationUpdatePackageVerifier.VerifyAsync(packagePath, packageName, $"{hash}  {packageName}", "1.5.0"));
    }

    [Fact]
    public async Task PackageVerifierRejectsUnsafeArchivePath()
    {
        using var directory = new TemporaryDirectory();
        const string packageName = "IsraeliAuthorStudio-macos-arm64.zip";
        var packagePath = Path.Combine(directory.Path, packageName);
        CreatePackage(packagePath, "1.5.0", includeUnsafePath: true);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))).ToLowerInvariant();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ApplicationUpdatePackageVerifier.VerifyAsync(packagePath, packageName, $"{hash}  {packageName}", "1.5.0"));
    }

    [Fact]
    public async Task UpdateServiceDownloadsAndVerifiesNewRelease()
    {
        using var directory = new TemporaryDirectory();
        const string packageName = "IsraeliAuthorStudio-macos-x64.zip";
        var sourcePackage = Path.Combine(directory.Path, "source.zip");
        CreatePackage(sourcePackage, "1.5.0");
        var packageBytes = await File.ReadAllBytesAsync(sourcePackage);
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var releaseJson = $$"""
            {
              "tag_name": "v1.5.0",
              "html_url": "https://github.com/shachar-roth/BookWriter/releases/tag/v1.5.0",
              "assets": [
                {
                  "name": "{{packageName}}",
                  "size": {{packageBytes.Length}},
                  "browser_download_url": "https://github.com/shachar-roth/BookWriter/releases/download/v1.5.0/{{packageName}}"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "size": 100,
                  "browser_download_url": "https://github.com/shachar-roth/BookWriter/releases/download/v1.5.0/SHA256SUMS.txt"
                }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Response("application/json", Encoding.UTF8.GetBytes(releaseJson));
            if (request.RequestUri.AbsolutePath.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
                return Response("text/plain", Encoding.UTF8.GetBytes($"{hash}  {packageName}"));
            return Response("application/zip", packageBytes);
        });
        var service = new ApplicationUpdateService(
            new StubHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }),
            new ApplicationDataPaths(Path.Combine(directory.Path, "data")),
            new ApplicationUpdateOptions { Enabled = true, CurrentVersion = new Version(1, 0, 0) },
            new StubApplicationLifetime(),
            NullLogger<ApplicationUpdateService>.Instance);

        await service.CheckAndDownloadAsync();

        Assert.Equal(ApplicationUpdateState.Ready, service.Snapshot.State);
        Assert.Equal("1.5.0", service.Snapshot.Version);
        Assert.True(File.Exists(Path.Combine(directory.Path, "data", "Updates", "1.5.0", packageName)));
        var notified = false;
        service.StateChanged += () => notified = true;
        await service.CheckAndDownloadAsync();
        Assert.True(notified);
        Assert.Equal(ApplicationUpdateState.Ready, service.Snapshot.State);
    }

    [Theory]
    [InlineData(404, ApplicationUpdateState.UpToDate)]
    [InlineData(503, ApplicationUpdateState.Failed)]
    [InlineData(200, ApplicationUpdateState.Failed)]
    public async Task ManualCheckReportsNoReleaseSeparatelyFromServerOrManifestErrors(int httpStatus, ApplicationUpdateState expected)
    {
        using var directory = new TemporaryDirectory();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage((System.Net.HttpStatusCode)httpStatus)
        {
            Content = new StringContent("{}")
        });
        var service = new ApplicationUpdateService(
            new StubHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }),
            new ApplicationDataPaths(directory.Path),
            new ApplicationUpdateOptions { Enabled = true, CurrentVersion = new Version(1, 2, 3) },
            new StubApplicationLifetime(), NullLogger<ApplicationUpdateService>.Instance);
        var states = new List<ApplicationUpdateState>();
        service.StateChanged += () => states.Add(service.Snapshot.State);

        await service.CheckAndDownloadAsync();

        Assert.Equal("1.2.3", service.CurrentVersion);
        Assert.Equal([ApplicationUpdateState.Checking, expected], states);
    }

    [Fact]
    public void InstallerScriptContainsStartupRollback()
    {
        Assert.Contains("health", MacApplicationUpdateInstaller.HelperScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rolled-back", MacApplicationUpdateInstaller.HelperScript, StringComparison.Ordinal);
        Assert.Contains("mv \"$BACKUP_PATH\" \"$APP_PATH\"", MacApplicationUpdateInstaller.HelperScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerScriptHasValidShellSyntaxOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var scriptPath = Path.Combine(directory.Path, "installer.sh");
        await File.WriteAllTextAsync(scriptPath, MacApplicationUpdateInstaller.HelperScript);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardError = true,
            ArgumentList = { "-n", scriptPath }
        });
        Assert.NotNull(process);
        await process.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static void CreatePackage(string path, string version, bool includeUnsafePath = false)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "Israeli Author Studio.app/Contents/MacOS/IsraeliAuthorStudio", "executable");
        WriteEntry(archive, "Israeli Author Studio.app/Contents/Info.plist", $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CFBundleShortVersionString</key><string>{{version}}</string>
            </dict></plist>
            """);
        if (includeUnsafePath) WriteEntry(archive, "Israeli Author Studio.app/../outside.txt", "unsafe");
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static HttpResponseMessage Response(string contentType, byte[] content) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
        }
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ias-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
