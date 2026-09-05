using IsraeliAuthorStudio.Models;
using IsraeliAuthorStudio.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace IsraeliAuthorStudio.Tests;

public sealed class GitRepositoryServiceTests
{
    private readonly GitRepositoryService _git = new(NullLogger<GitRepositoryService>.Instance);

    [Fact]
    public async Task EnsureRepositoryCreatesInitialCommitAndIgnoreRules()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "Scenes"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "Scenes", "one.scene.md"), "scene");

        var result = await _git.EnsureProjectRepositoryAsync(root.Path, "Test Writer", "writer@example.test");

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(Path.Combine(root.Path, ".git")));
        Assert.Contains(".history/", await File.ReadAllTextAsync(Path.Combine(root.Path, ".gitignore")));
        Assert.True((await _git.GetHeadAsync(root.Path)).Success);
    }

    [Fact]
    public async Task SnapshotPushesToBareRemoteAndCloneValidatesStory()
    {
        using var root = new TemporaryDirectory();
        var project = Path.Combine(root.Path, "project");
        var remote = Path.Combine(root.Path, "remote.git");
        var clone = Path.Combine(root.Path, "clone");
        Directory.CreateDirectory(Path.Combine(project, "Scenes"));
        await File.WriteAllTextAsync(Path.Combine(project, "Scenes", "one.scene.md"), "first");
        Assert.True((await _git.EnsureProjectRepositoryAsync(project, "Test Writer", "writer@example.test")).Success);
        Assert.True((await RunGitAsync(root.Path, "init", "--bare", remote)).Success);
        Assert.True((await _git.AttachRemoteAsync(project, remote)).Success);

        await File.WriteAllTextAsync(Path.Combine(project, "Scenes", "one.scene.md"), "second");
        var sync = await _git.SnapshotAndSyncAsync(project, "autosave: test");
        var cloneResult = await _git.CloneAsync(remote, clone);

        Assert.Equal(SyncState.Pushed, sync.State);
        Assert.True(cloneResult.Success, cloneResult.Error);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(clone, "Scenes", "one.scene.md")));
    }

    [Fact]
    public async Task ConflictingRemoteChangeAbortsRebaseAndPreservesLocalFile()
    {
        using var root = new TemporaryDirectory();
        var project = Path.Combine(root.Path, "project");
        var remote = Path.Combine(root.Path, "remote.git");
        var other = Path.Combine(root.Path, "other");
        Directory.CreateDirectory(Path.Combine(project, "Scenes"));
        var scenePath = Path.Combine(project, "Scenes", "one.scene.md");
        await File.WriteAllTextAsync(scenePath, "base");
        Assert.True((await _git.EnsureProjectRepositoryAsync(project, "Test Writer", "writer@example.test")).Success);
        Assert.True((await RunGitAsync(root.Path, "init", "--bare", remote)).Success);
        Assert.True((await _git.AttachRemoteAsync(project, remote)).Success);
        Assert.Equal(SyncState.Pushed, (await _git.SnapshotAndSyncAsync(project, "initial push")).State);

        Assert.True((await _git.CloneAsync(remote, other)).Success);
        await RunGitAsync(other, "config", "user.name", "Other Writer");
        await RunGitAsync(other, "config", "user.email", "other@example.test");
        await File.WriteAllTextAsync(Path.Combine(other, "Scenes", "one.scene.md"), "remote");
        await RunGitAsync(other, "add", "--all");
        await RunGitAsync(other, "commit", "-m", "remote edit");
        await RunGitAsync(other, "push");

        await File.WriteAllTextAsync(scenePath, "local");
        var sync = await _git.SnapshotAndSyncAsync(project, "local edit");

        Assert.Equal(SyncState.Conflict, sync.State);
        Assert.Equal("local", await File.ReadAllTextAsync(scenePath));
        Assert.False(Directory.Exists(Path.Combine(project, ".git", "rebase-merge")));
    }

    [Fact]
    public async Task AssistantUndoIgnoresGeneratedTimestampOnlyAutosave()
    {
        using var root = new TemporaryDirectory();
        var sceneDirectory = Path.Combine(root.Path, "Scenes");
        var scenePath = Path.Combine(sceneDirectory, "one.scene.md");
        Directory.CreateDirectory(sceneDirectory);
        await File.WriteAllTextAsync(scenePath, SceneMarkdown("2026-01-01T00:00:00Z", "original"));
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Test Writer", "writer@example.test")).Success);

        await File.WriteAllTextAsync(scenePath, SceneMarkdown("2026-01-02T00:00:00Z", "assistant edit"));
        Assert.True((await _git.CommitAsync(root.Path, "assistant: edit")).Success);
        var assistantCommit = (await _git.GetHeadAsync(root.Path)).Output.Trim();
        await File.WriteAllTextAsync(scenePath, SceneMarkdown("2026-01-03T00:00:00Z", "assistant edit"));

        var result = await _git.RevertPreservingLocalChangesAsync(root.Path, assistantCommit);

        Assert.True(result.Success, result.Error);
        Assert.Contains("original", await File.ReadAllTextAsync(scenePath));
        Assert.DoesNotContain("assistant edit", await File.ReadAllTextAsync(scenePath));
        Assert.True(string.IsNullOrWhiteSpace((await _git.GetStatusAsync(root.Path)).Output));
    }

    [Fact]
    public async Task AssistantUndoAbortsConflictAndPreservesLaterWriting()
    {
        using var root = new TemporaryDirectory();
        var sceneDirectory = Path.Combine(root.Path, "Scenes");
        var scenePath = Path.Combine(sceneDirectory, "one.scene.md");
        Directory.CreateDirectory(sceneDirectory);
        await File.WriteAllTextAsync(scenePath, SceneMarkdown("2026-01-01T00:00:00Z", "original"));
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Test Writer", "writer@example.test")).Success);

        await File.WriteAllTextAsync(scenePath, SceneMarkdown("2026-01-02T00:00:00Z", "assistant edit"));
        Assert.True((await _git.CommitAsync(root.Path, "assistant: edit")).Success);
        var assistantCommit = (await _git.GetHeadAsync(root.Path)).Output.Trim();
        await File.WriteAllTextAsync(scenePath, SceneMarkdown("2026-01-03T00:00:00Z", "later writer edit"));

        var result = await _git.RevertPreservingLocalChangesAsync(root.Path, assistantCommit);

        Assert.False(result.Success);
        Assert.Contains("later writer edit", await File.ReadAllTextAsync(scenePath));
        Assert.DoesNotContain("<<<<<<<", await File.ReadAllTextAsync(scenePath));
        Assert.False(File.Exists(Path.Combine(root.Path, ".git", "REVERT_HEAD")));
        Assert.True(string.IsNullOrWhiteSpace((await _git.GetStatusAsync(root.Path)).Output));
    }

    private static string SceneMarkdown(string updatedAt, string content) => $"""
        ---
        id: scn-one
        updatedAt: {updatedAt}
        ---

        {content}
        """;

    private static async Task<GitOperationResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitOperationResult(process.ExitCode == 0, output, error, process.ExitCode);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"IsraeliAuthorStudio-git-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
    }
}
