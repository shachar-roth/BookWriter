using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IsraeliAuthorStudio.Models;

namespace IsraeliAuthorStudio.Services;

public sealed partial class GitRepositoryService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] IgnoreEntries =
    [
        ".history/",
        ".studio/cache/",
        "*.tmp-*",
        "assistant-settings.json",
        "current-project.json"
    ];

    private readonly ILogger<GitRepositoryService> _logger;

    public GitRepositoryService(ILogger<GitRepositoryService> logger)
    {
        _logger = logger;
    }

    public async Task<GitOperationResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(null, ["--version"], cancellationToken, TimeSpan.FromSeconds(10));

    public async Task<GitOperationResult> EnsureProjectRepositoryAsync(
        string projectPath,
        string? authorName = null,
        string? authorEmail = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(fullPath);

        var discovered = await RunAsync(fullPath, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (discovered.Success)
        {
            var root = NormalizePath(discovered.Output.Trim());
            if (!string.Equals(root, NormalizePath(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                return GitOperationResult.Failed("The project is inside another Git repository. Select the repository root or move the project to a separate folder.");
            }
        }
        else
        {
            var initialized = await RunAsync(fullPath, ["init"], cancellationToken);
            if (!initialized.Success) return initialized;
            var branch = await RunAsync(fullPath, ["symbolic-ref", "HEAD", "refs/heads/main"], cancellationToken);
            if (!branch.Success) return branch;
        }

        await EnsureProjectFilesAsync(fullPath, cancellationToken);
        var identity = await EnsureIdentityAsync(fullPath, authorName, authorEmail, cancellationToken);
        if (!identity.Success) return identity;

        var head = await RunAsync(fullPath, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        if (!head.Success)
        {
            var commit = await CommitAsync(fullPath, "Initial story project", cancellationToken);
            if (!commit.Success) return commit;
        }

        return new GitOperationResult(true, "Repository ready.");
    }

    public async Task<GitOperationResult> CloneAsync(
        string remoteUrl,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return GitOperationResult.Failed("A remote URL is required.");
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(fullPath);
        if (Directory.EnumerateFileSystemEntries(fullPath).Any()) return GitOperationResult.Failed("The destination folder must be empty.");

        var result = await RunAsync(null, ["clone", remoteUrl.Trim(), fullPath], cancellationToken, TimeSpan.FromMinutes(5));
        if (!result.Success) return result;
        if (!Directory.Exists(Path.Combine(fullPath, "Scenes")))
        {
            var main = await RunAsync(fullPath, ["show-ref", "--verify", "refs/remotes/origin/main"], cancellationToken);
            if (main.Success)
            {
                var checkout = await RunAsync(fullPath, ["checkout", "-B", "main", "origin/main"], cancellationToken);
                if (!checkout.Success) return checkout;
            }
            if (!Directory.Exists(Path.Combine(fullPath, "Scenes")))
            {
                return GitOperationResult.Failed("The cloned repository is not a story project because it has no Scenes folder.");
            }
        }

        return result;
    }

    public async Task<GitOperationResult> AttachRemoteAsync(
        string projectPath,
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return GitOperationResult.Failed("A remote URL is required.");
        var existing = await RunAsync(projectPath, ["remote", "get-url", "origin"], cancellationToken);
        var result = existing.Success
            ? await RunAsync(projectPath, ["remote", "set-url", "origin", remoteUrl.Trim()], cancellationToken)
            : await RunAsync(projectPath, ["remote", "add", "origin", remoteUrl.Trim()], cancellationToken);
        if (!result.Success) return result;

        var fetch = await RunAsync(projectPath, ["fetch", "origin"], cancellationToken, TimeSpan.FromMinutes(2));
        if (!fetch.Success && !LooksLikeEmptyRemote(fetch.Error)) return fetch;

        var branch = await GetCurrentBranchAsync(projectPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch)) return GitOperationResult.Failed("The project does not have a current branch.");
        var remoteBranch = await RunAsync(projectPath, ["show-ref", "--verify", $"refs/remotes/origin/{branch}"], cancellationToken);
        if (remoteBranch.Success)
        {
            var mergeBase = await RunAsync(projectPath, ["merge-base", "HEAD", $"origin/{branch}"], cancellationToken);
            if (!mergeBase.Success)
            {
                await RunAsync(projectPath, ["remote", "remove", "origin"], cancellationToken);
                return GitOperationResult.Failed("The remote contains unrelated history. Clone it into a new folder instead.");
            }
        }

        return new GitOperationResult(true, remoteUrl.Trim());
    }

    public async Task<GitOperationResult> CommitAsync(
        string projectPath,
        string message,
        CancellationToken cancellationToken = default)
    {
        var discovered = await RunAsync(projectPath, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (!discovered.Success) return discovered;
        if (!string.Equals(NormalizePath(discovered.Output.Trim()), NormalizePath(Path.GetFullPath(projectPath)), StringComparison.OrdinalIgnoreCase))
            return GitOperationResult.Failed("The project is inside another Git repository. Automatic snapshots cannot use the parent repository.");

        var status = await GetStatusAsync(projectPath, cancellationToken);
        if (!status.Success) return status;
        if (string.IsNullOrWhiteSpace(status.Output)) return new GitOperationResult(true, "No changes.");

        var add = await RunAsync(projectPath, ["add", "--all"], cancellationToken);
        if (!add.Success) return add;
        return await RunAsync(projectPath, ["commit", "-m", message], cancellationToken);
    }

    public Task<GitOperationResult> GetStatusAsync(string projectPath, CancellationToken cancellationToken = default) =>
        RunAsync(projectPath, ["status", "--porcelain=v1"], cancellationToken);

    public Task<GitOperationResult> GetRemoteUrlAsync(string projectPath, CancellationToken cancellationToken = default) =>
        RunAsync(projectPath, ["remote", "get-url", "origin"], cancellationToken);

    public Task<GitOperationResult> GetHeadAsync(string projectPath, CancellationToken cancellationToken = default) =>
        RunAsync(projectPath, ["rev-parse", "HEAD"], cancellationToken);

    public async Task<SyncResult> SnapshotAndSyncAsync(
        string projectPath,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        var commit = await CommitAsync(projectPath, commitMessage, cancellationToken);
        if (!commit.Success)
        {
            if (commit.Error.Contains("identity", StringComparison.OrdinalIgnoreCase) || commit.Error.Contains("user.email", StringComparison.OrdinalIgnoreCase))
                return new SyncResult(SyncState.NeedsIdentity, commit.Error);
            return new SyncResult(SyncState.Failed, FriendlyError(commit));
        }

        var head = await GetHeadAsync(projectPath, cancellationToken);
        var commitHash = head.Success ? head.Output.Trim() : null;
        var remote = await GetRemoteUrlAsync(projectPath, cancellationToken);
        if (!remote.Success)
        {
            return new SyncResult(commit.Output.Contains("No changes", StringComparison.Ordinal) ? SyncState.UpToDate : SyncState.NoRemote,
                "Saved locally. No remote is configured.", commitHash);
        }

        var fetch = await RunAsync(projectPath, ["fetch", "origin"], cancellationToken, TimeSpan.FromMinutes(2));
        if (!fetch.Success) return ClassifyNetworkFailure(fetch, commitHash);

        var branch = await GetCurrentBranchAsync(projectPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch)) return new SyncResult(SyncState.Failed, "No current branch was found.", commitHash);
        var remoteBranch = await RunAsync(projectPath, ["show-ref", "--verify", $"refs/remotes/origin/{branch}"], cancellationToken);
        if (remoteBranch.Success)
        {
            var rebase = await RunAsync(projectPath, ["rebase", $"origin/{branch}"], cancellationToken, TimeSpan.FromMinutes(2));
            if (!rebase.Success)
            {
                await RunAsync(projectPath, ["rebase", "--abort"], cancellationToken);
                return new SyncResult(SyncState.Conflict, "Remote changes conflict with local changes. Automatic sync is paused.", commitHash);
            }
        }

        var pushArguments = remoteBranch.Success
            ? new[] { "push", "origin", branch }
            : new[] { "push", "-u", "origin", branch };
        var push = await RunAsync(projectPath, pushArguments, cancellationToken, TimeSpan.FromMinutes(2));
        if (!push.Success) return ClassifyNetworkFailure(push, commitHash);
        return new SyncResult(SyncState.Pushed, "Changes were committed and synchronized.", commitHash);
    }

    public async Task<GitOperationResult> RevertAsync(string projectPath, string commitHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commitHash)) return GitOperationResult.Failed("A commit hash is required.");
        return await RunAsync(projectPath, ["revert", "--no-edit", commitHash], cancellationToken, TimeSpan.FromMinutes(2));
    }

    public async Task<GitOperationResult> RevertPreservingLocalChangesAsync(
        string projectPath,
        string commitHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commitHash)) return GitOperationResult.Failed("A commit hash is required.");

        var normalize = await RestoreGeneratedSceneTimestampChangesAsync(projectPath, cancellationToken);
        if (!normalize.Success) return normalize;

        var snapshot = await CommitAsync(projectPath, "Autosave before assistant undo", cancellationToken);
        if (!snapshot.Success) return snapshot;

        var revert = await RevertAsync(projectPath, commitHash, cancellationToken);
        if (revert.Success) return revert;

        await RunAsync(projectPath, ["revert", "--abort"], cancellationToken);
        var details = string.IsNullOrWhiteSpace(revert.Error) ? revert.Output : revert.Error;
        return GitOperationResult.Failed(
            $"The assistant change conflicts with edits made afterward. Those later edits were saved in Git and were not lost. {details}".Trim());
    }

    public Task<GitOperationResult> UnstageAsync(string projectPath, CancellationToken cancellationToken = default) =>
        RunAsync(projectPath, ["reset"], cancellationToken);

    private async Task<GitOperationResult> RestoreGeneratedSceneTimestampChangesAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var changedScenes = await RunAsync(
            projectPath,
            ["diff", "--name-only", "--diff-filter=M", "HEAD", "--", "Scenes"],
            cancellationToken);
        if (!changedScenes.Success) return changedScenes;

        foreach (var relativePath in changedScenes.Output
                     .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(path => path.EndsWith(".scene.md", StringComparison.OrdinalIgnoreCase)))
        {
            var headFile = await RunAsync(
                projectPath,
                ["show", $"HEAD:{relativePath.Replace('\\', '/')}"],
                cancellationToken);
            if (!headFile.Success) return headFile;

            var filePath = Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var workingFile = await File.ReadAllTextAsync(filePath, cancellationToken);
            if (!string.Equals(
                    WithoutGeneratedSceneTimestamp(headFile.Output),
                    WithoutGeneratedSceneTimestamp(workingFile),
                    StringComparison.Ordinal))
            {
                continue;
            }

            var restore = await RunAsync(
                projectPath,
                ["restore", "--source=HEAD", "--staged", "--worktree", "--", relativePath],
                cancellationToken);
            if (!restore.Success) return restore;
        }

        return new GitOperationResult(true);
    }

    private static string WithoutGeneratedSceneTimestamp(string value) =>
        string.Join(
                '\n',
                value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n')
                    .Where(line => !line.StartsWith("updatedAt:", StringComparison.Ordinal)))
            .TrimEnd();

    private async Task<GitOperationResult> EnsureIdentityAsync(
        string projectPath,
        string? authorName,
        string? authorEmail,
        CancellationToken cancellationToken)
    {
        var name = await RunAsync(projectPath, ["config", "user.name"], cancellationToken);
        var email = await RunAsync(projectPath, ["config", "user.email"], cancellationToken);
        if (!name.Success || string.IsNullOrWhiteSpace(name.Output))
        {
            if (string.IsNullOrWhiteSpace(authorName)) return GitOperationResult.Failed("Git author identity is missing. Configure a name and email.");
            var configured = await RunAsync(projectPath, ["config", "user.name", authorName.Trim()], cancellationToken);
            if (!configured.Success) return configured;
        }
        if (!email.Success || string.IsNullOrWhiteSpace(email.Output))
        {
            if (string.IsNullOrWhiteSpace(authorEmail)) return GitOperationResult.Failed("Git author identity is missing. Configure a name and email.");
            var configured = await RunAsync(projectPath, ["config", "user.email", authorEmail.Trim()], cancellationToken);
            if (!configured.Success) return configured;
        }
        return new GitOperationResult(true);
    }

    private static async Task EnsureProjectFilesAsync(string projectPath, CancellationToken cancellationToken)
    {
        var ignorePath = Path.Combine(projectPath, ".gitignore");
        var existing = File.Exists(ignorePath) ? await File.ReadAllLinesAsync(ignorePath, cancellationToken) : [];
        var entries = existing.ToList();
        foreach (var ignore in IgnoreEntries)
        {
            if (!entries.Contains(ignore, StringComparer.Ordinal)) entries.Add(ignore);
        }
        await File.WriteAllLinesAsync(ignorePath, entries, new UTF8Encoding(false), cancellationToken);

        var studioDirectory = Path.Combine(projectPath, ".studio");
        Directory.CreateDirectory(studioDirectory);
        var manifestPath = Path.Combine(studioDirectory, "project.json");
        if (!File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectId = Guid.NewGuid().ToString("N"),
                createdAt = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false), cancellationToken);
        }
    }

    private async Task<string?> GetCurrentBranchAsync(string projectPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(projectPath, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        return result.Success ? result.Output.Trim() : null;
    }

    private async Task<GitOperationResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return new GitOperationResult(process.ExitCode == 0, output, error, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GitOperationResult.Failed("Git operation timed out.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Git invocation failed for {Arguments}", string.Join(' ', arguments));
            return GitOperationResult.Failed("Git is not installed or could not be started.");
        }
    }

    private static SyncResult ClassifyNetworkFailure(GitOperationResult result, string? commitHash)
    {
        var text = $"{result.Output}\n{result.Error}";
        if (text.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not read Username", StringComparison.OrdinalIgnoreCase))
            return new SyncResult(SyncState.AuthenticationFailed, "Remote authentication failed. Local commits are safe.", commitHash);
        if (text.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to access", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Connection", StringComparison.OrdinalIgnoreCase))
            return new SyncResult(SyncState.Offline, "The remote is unavailable. Local commits are safe and sync will retry.", commitHash);
        return new SyncResult(SyncState.Failed, FriendlyError(result), commitHash);
    }

    private static string FriendlyError(GitOperationResult result) =>
        string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;

    private static bool LooksLikeEmptyRemote(string error) =>
        error.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
}
