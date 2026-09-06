using IsraeliAuthorStudio.Models;
using IsraeliAuthorStudio.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace IsraeliAuthorStudio.Tests;

public sealed class GitRepositoryServiceTests
{
    private readonly GitRepositoryService _git = new(NullLogger<GitRepositoryService>.Instance);

    [Fact]
    public async Task HistoryReadsDeletedSceneAndDiffsWithoutChangingWorktreeOrIndex()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "Scenes"));
        var scenePath = Path.Combine(root.Path, "Scenes", "scn-one.scene.md");
        await File.WriteAllTextAsync(scenePath, "original scene\n");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "private.txt"), "private secret");
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Writer", "writer@example.test")).Success);
        var originalHash = (await _git.GetHeadAsync(root.Path)).Output.Trim();
        await File.WriteAllTextAsync(scenePath, "edited scene\n");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "private.txt"), "changed private secret");
        Assert.True((await _git.CommitAsync(root.Path, "Edit scene")).Success);
        var editHash = (await _git.GetHeadAsync(root.Path)).Output.Trim();
        File.Delete(scenePath);
        Assert.True((await _git.CommitAsync(root.Path, "Delete scene")).Success);
        var head = (await _git.GetHeadAsync(root.Path)).Output;
        var otherScene = Path.Combine(root.Path, "Scenes", "scn-two.scene.md");
        await File.WriteAllTextAsync(otherScene, "staged draft");
        await RunGitAsync(root.Path, "add", "--", "Scenes/scn-two.scene.md");
        await File.WriteAllTextAsync(otherScene, "unstaged draft");
        var statusBefore = (await _git.GetStatusAsync(root.Path)).Output;
        var indexBefore = await File.ReadAllBytesAsync(Path.Combine(root.Path, ".git", "index"));

        var history = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.History, "scn-one", count: 1);
        var older = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.History, "scn-one", skip: 1, count: 1);
        var previous = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.SceneAtCommit, "scn-one", originalHash);
        var diff = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.CommitDiff, commitHash: editHash);
        var working = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.WorkingDiff);
        var status = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.Status);

        Assert.True(history.Success, history.Error);
        Assert.Contains("Delete scene", history.Output);
        Assert.DoesNotContain("Edit scene", history.Output);
        Assert.Contains(editHash, older.Output);
        Assert.Equal("original scene\n", previous.Output);
        Assert.Contains("-original scene", diff.Output);
        Assert.Contains("+edited scene", diff.Output);
        Assert.DoesNotContain("private", diff.Output);
        Assert.Contains("unstaged draft", working.Output);
        Assert.DoesNotContain("private.txt", status.Output);
        Assert.Equal(head, (await _git.GetHeadAsync(root.Path)).Output);
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(Path.Combine(root.Path, ".git", "index")));
        Assert.Equal(statusBefore, (await _git.GetStatusAsync(root.Path)).Output);
        Assert.False(File.Exists(scenePath));
        Assert.Equal("unstaged draft", await File.ReadAllTextAsync(otherScene));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryPagesLongSceneExactlyAndDisablesExternalGitHelpers(bool splitSurrogate)
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "Scenes"));
        var text = splitSurrogate ? new string('a', 11999) + "\U0001F600" + new string('b', 13000) : "  " + new string('a', 25000) + "\n  ";
        var path = Path.Combine(root.Path, "Scenes", "scn-one.scene.md");
        await File.WriteAllTextAsync(path, text);
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Writer", "writer@example.test")).Success);
        var hash = (await _git.GetHeadAsync(root.Path)).Output.Trim();
        await RunGitAsync(root.Path, "config", "core.fsmonitor", "must-not-execute-monitor");
        await RunGitAsync(root.Path, "config", "diff.external", "must-not-execute-diff");
        await RunGitAsync(root.Path, "config", "filter.unsafe.clean", "must-not-execute-clean");
        await RunGitAsync(root.Path, "config", "filter.unsafe.process", "must-not-execute-process");
        await RunGitAsync(root.Path, "config", "filter.unsafe.required", "true");
        await File.WriteAllTextAsync(Path.Combine(root.Path, ".gitattributes"), "*.scene.md filter=unsafe\n");
        await File.WriteAllTextAsync(path, "changed");

        var first = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.SceneAtCommit, "scn-one", hash);
        var second = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.SceneAtCommit, "scn-one", hash, offset: first.NextOffset!.Value);
        var third = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.SceneAtCommit, "scn-one", hash, offset: second.NextOffset!.Value);

        Assert.Equal(splitSurrogate ? 11999 : 12000, first.Output.Length);
        Assert.Equal(text, first.Output + second.Output + third.Output);
        Assert.Null(third.NextOffset);
        Assert.True((await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.WorkingDiff)).Success);
        Assert.True((await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.Status)).Success);
        Assert.True((await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.CommitDiff, commitHash: hash)).Success);
    }

    [Theory]
    [InlineData("../../private", "1234567")]
    [InlineData("scn-one/../../private", "1234567")]
    [InlineData("scn-*", "1234567")]
    [InlineData("scn-one", "HEAD:private.txt")]
    [InlineData("scn-one", "--output=private.txt")]
    [InlineData("scn-one", "HEAD~1")]
    public async Task HistoryRejectsPathsWildcardsAndRevisionExpressions(string sceneId, string revision)
    {
        using var root = new TemporaryDirectory();
        var result = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.SceneAtCommit, sceneId, revision);
        Assert.False(result.Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task HistoryRejectsAncestorRepositoriesAndMissingRepositoriesWithoutInitializing()
    {
        using var root = new TemporaryDirectory();
        Assert.False((await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.History)).Success);
        Assert.False(Directory.Exists(Path.Combine(root.Path, ".git")));
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Writer", "writer@example.test")).Success);
        var nested = Directory.CreateDirectory(Path.Combine(root.Path, "nested")).FullName;
        var result = await _git.ReadHistoryAsync(nested, GitHistoryOperation.History);
        Assert.False(result.Success);
        Assert.Contains("parent repository", result.Error);
        Assert.False(Directory.Exists(Path.Combine(nested, ".git")));
    }

    [Fact]
    public async Task HistoryHonorsCancellationAndRejectsUnreachableCommits()
    {
        using var root = new TemporaryDirectory();
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Writer", "writer@example.test")).Success);
        await RunGitAsync(root.Path, "checkout", "--orphan", "unrelated");
        await RunGitAsync(root.Path, "commit", "--allow-empty", "-m", "Unrelated history");
        var unrelated = (await _git.GetHeadAsync(root.Path)).Output.Trim();
        await RunGitAsync(root.Path, "checkout", "main");

        var rejected = await _git.ReadHistoryAsync(root.Path, GitHistoryOperation.CommitDiff, commitHash: unrelated);
        Assert.False(rejected.Success);
        Assert.Contains("not part", rejected.Error);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _git.ReadHistoryAsync(root.Path, GitHistoryOperation.History, cancellationToken: cancel.Token));
    }

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
    public async Task SnapshotRefusesAncestorRepositoryWithoutCommittingItsChanges()
    {
        using var root = new TemporaryDirectory();
        Assert.True((await _git.EnsureProjectRepositoryAsync(root.Path, "Test Writer", "writer@example.test")).Success);
        var originalHead = (await _git.GetHeadAsync(root.Path)).Output;
        var project = Path.Combine(root.Path, "nested-story");
        Directory.CreateDirectory(Path.Combine(project, "Scenes"));
        await File.WriteAllTextAsync(Path.Combine(project, "Scenes", "one.scene.md"), "nested manuscript");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "unrelated.txt"), "unrelated change");
        var originalStatus = (await _git.GetStatusAsync(root.Path)).Output;

        var result = await _git.SnapshotAndSyncAsync(project, "must not commit parent");

        Assert.False(result.IsSuccess);
        Assert.Equal(originalHead, (await _git.GetHeadAsync(root.Path)).Output);
        Assert.Equal(originalStatus, (await _git.GetStatusAsync(root.Path)).Output);
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
