using System.IO.Compression;
using IsraeliAuthorStudio.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IsraeliAuthorStudio.Tests;

public sealed class DiagnosticBundleTests
{
    [Fact]
    public void LocalLoggerWritesErrorsAndRedactsCredentials()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var applicationData = new ApplicationDataPaths(Path.Combine(root, "AppData"));
            using (var factory = LoggerFactory.Create(builder =>
                   {
                       builder.SetMinimumLevel(LogLevel.Information);
                       builder.AddProvider(new LocalFileLoggerProvider(applicationData));
                   }))
            {
                var exceptionApiKey = "sk-" + "secretkey123456789";
                var messageApiKey = "sk-" + "anothersecret987654321";
                factory.CreateLogger("DiagnosticTest").LogError(
                    new InvalidOperationException($"Bearer {exceptionApiKey}"),
                    "Request failed with {ApiKey}",
                    messageApiKey);
            }

            var content = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(
                Path.Combine(applicationData.RootPath, "Logs"),
                "studio-*.log")));
            Assert.Contains("Request failed", content);
            Assert.Contains("[REDACTED_API_KEY]", content);
            Assert.DoesNotContain("secretkey", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("anothersecret", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiagnosticBundleIncludesProjectAndLogsButExcludesSecretsAndGitInternals()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var applicationData = new ApplicationDataPaths(Path.Combine(root, "AppData"));
            var projectRoot = Path.Combine(root, "Story");
            Directory.CreateDirectory(Path.Combine(projectRoot, "Scenes"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Metadata", "Scenes"));
            Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
            Directory.CreateDirectory(Path.Combine(applicationData.RootPath, "Logs"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Scenes", "scn-one.scene.md"), "scene text");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Metadata", "Scenes", "scn-one.json"), "{\"sceneId\":\"scn-one\"}");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, ".git", "config"), "private remote");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, ".env"), "OPENAI_API_KEY=secret");
            await File.WriteAllTextAsync(Path.Combine(applicationData.RootPath, "Logs", "studio-20260830.log"), "diagnostic log");

            var environment = new TestWebHostEnvironment(root);
            var projects = new ProjectSelectionService(environment, applicationData);
            await projects.SetCurrentProjectPathAsync(projectRoot);
            var service = new DiagnosticBundleService(
                applicationData,
                projects,
                NullLogger<DiagnosticBundleService>.Instance);

            var bundle = await service.CreateAsync();
            using var archive = new ZipArchive(new MemoryStream(bundle.Content), ZipArchiveMode.Read);
            var names = archive.Entries.Select(entry => entry.FullName).ToList();

            Assert.Contains("project/Scenes/scn-one.scene.md", names);
            Assert.Contains("project/Metadata/Scenes/scn-one.json", names);
            Assert.Contains("diagnostics/logs/studio-20260830.log", names);
            Assert.Contains("diagnostics/manifest.json", names);
            Assert.DoesNotContain(names, name => name.Contains("/.git/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("project/.env", names);
            Assert.EndsWith(".zip", bundle.FileName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IsraeliAuthorStudio-diagnostics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
