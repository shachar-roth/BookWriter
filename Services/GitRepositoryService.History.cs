using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IsraeliAuthorStudio.Services;

public enum GitHistoryOperation { Status, History, CommitDiff, WorkingDiff, SceneAtCommit }

public sealed record GitHistoryResult(bool Success, string Output = "", string Error = "", int? NextOffset = null);

public sealed partial class GitRepositoryService
{
    private const int HistoryPageSize = 12000;
    private static readonly string[] ManuscriptPaths =
    [
        ":(top,glob)Scenes/*.scene.md", ":(top,literal)Indexes/chapters.json",
        ":(top,literal)Indexes/characters.json", ":(top,literal)Indexes/locations.json",
        ":(top,literal)Indexes/timeline.json", ":(top,glob)Metadata/Scenes/*.json"
    ];

    public async Task<GitHistoryResult> ReadHistoryAsync(
        string projectPath, GitHistoryOperation operation, string? sceneId = null, string? commitHash = null,
        int skip = 0, int count = 10, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(operation) || skip is < 0 or > 10000 || count is < 1 or > 20 || offset is < 0 or > 2000000)
            return new(false, Error: "Invalid operation or pagination. Use count 1-20, skip 0-10000 and offset 0-2000000.");
        if (sceneId is not null && !Regex.IsMatch(sceneId, @"\Ascn-[A-Za-z0-9_-]{1,80}\z"))
            return new(false, Error: "Use a scene ID, not a filename or path.");
        if (operation == GitHistoryOperation.SceneAtCommit && sceneId is null)
            return new(false, Error: "A scene ID is required.");
        if (operation is GitHistoryOperation.CommitDiff or GitHistoryOperation.SceneAtCommit &&
            (commitHash is null || !Regex.IsMatch(commitHash, @"\A[0-9a-fA-F]{7,64}\z")))
            return new(false, Error: "Use a commit hash returned by History, not a branch, command or revision expression.");

        var root = await RunHistoryCommandAsync(projectPath, ["rev-parse", "--show-toplevel"], 0, cancellationToken);
        if (!root.Success) return root;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(NormalizePath(root.Output.Trim()), NormalizePath(projectPath), comparison))
            return new(false, Error: "This project is inside another repository. Reading the parent repository is not allowed.");

        var paths = sceneId is null ? ManuscriptPaths : new[] { $":(top,literal)Scenes/{sceneId}.scene.md" };
        if (operation == GitHistoryOperation.Status)
            return await ReadHistoryWorktreeCommandAsync(projectPath,
                ["status", "--porcelain=v1", "--branch", "--untracked-files=normal", "--", .. paths], offset, cancellationToken);

        var head = await RunHistoryCommandAsync(projectPath, ["rev-parse", "--verify", "HEAD"], 0, cancellationToken);
        if (!head.Success) return new(false, Error: "No readable saved commits are available in this project's local Git repository.");
        var revision = head.Output.Trim();
        if (operation is GitHistoryOperation.CommitDiff or GitHistoryOperation.SceneAtCommit)
        {
            var resolved = await RunHistoryCommandAsync(projectPath,
                ["rev-parse", "--verify", $"{commitHash}^{{commit}}"], 0, cancellationToken);
            if (!resolved.Success) return new(false, Error: "Commit not found or ambiguous. Use a full hash from History.");
            revision = resolved.Output.Trim();
            var reachable = await RunHistoryCommandAsync(projectPath,
                ["merge-base", "--is-ancestor", revision, head.Output.Trim()], 0, cancellationToken);
            if (!reachable.Success) return new(false, Error: "The commit is not part of the current project's branch history.");
        }

        string[] arguments = operation switch
        {
            GitHistoryOperation.History => ["log", "--no-color", "--no-decorate", "--no-renames",
                "--format=commit %H%nDate: %aI%nSubject: %s", "--name-status",
                $"--max-count={count.ToString(CultureInfo.InvariantCulture)}", $"--skip={skip.ToString(CultureInfo.InvariantCulture)}",
                revision, "--", .. paths],
            GitHistoryOperation.CommitDiff => ["show", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames",
                "--format=commit %H%nDate: %aI%nSubject: %s", "--patch", revision, "--", .. paths],
            GitHistoryOperation.WorkingDiff => ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames",
                revision, "--", .. paths],
            GitHistoryOperation.SceneAtCommit => ["show", "--no-ext-diff", "--no-textconv", $"{revision}:Scenes/{sceneId}.scene.md"],
            _ => throw new InvalidOperationException()
        };
        return operation == GitHistoryOperation.WorkingDiff
            ? await ReadHistoryWorktreeCommandAsync(projectPath, arguments, offset, cancellationToken)
            : await RunHistoryCommandAsync(projectPath, arguments, offset, cancellationToken);
    }

    private static async Task<GitHistoryResult> ReadHistoryWorktreeCommandAsync(
        string projectPath, string[] arguments, int offset, CancellationToken cancellationToken)
    {
        // Comparing working files can invoke clean/process filters even with --no-ext-diff.
        // Read configuration names only, never credential-bearing values, and disable those filters.
        var config = await RunHistoryCommandAsync(projectPath, ["config", "--null", "--name-only", "--list"], 0, cancellationToken);
        if (!config.Success || config.NextOffset is not null)
            return new(false, Error: "Cannot safely inspect working files with this Git configuration. Saved history is still available.");
        var overrides = new List<string>();
        foreach (var key in config.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .Where(key => key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!key.EndsWith(".clean", StringComparison.OrdinalIgnoreCase) &&
                !key.EndsWith(".process", StringComparison.OrdinalIgnoreCase) &&
                !key.EndsWith(".required", StringComparison.OrdinalIgnoreCase)) continue;
            overrides.Add("-c");
            overrides.Add(key + (key.EndsWith(".required", StringComparison.OrdinalIgnoreCase) ? "=false" : "="));
        }
        return await RunHistoryCommandAsync(projectPath, [.. overrides, .. arguments], offset, cancellationToken);
    }

    private static async Task<GitHistoryResult> RunHistoryCommandAsync(
        string projectPath, string[] arguments, int offset, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = projectPath, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        // No shell, pager, optional index writes, external diff drivers or filesystem-monitor hooks.
        foreach (var argument in new[] { "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.quotePath=false" }.Concat(arguments))
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            process.StartInfo.Environment.Remove(variable);
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
        try
        {
            process.Start();
            var outputTask = ReadHistoryPageAsync(process.StandardOutput, offset, HistoryPageSize, timeout.Token);
            var errorTask = ReadHistoryPageAsync(process.StandardError, 0, 1000, timeout.Token);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
                return new(false, Error: "Local Git could not read the requested data. The repository, commit or scene may be unavailable.");
            var output = await outputTask;
            return new(true, output.Text, NextOffset: output.HasMore ? offset + output.Text.Length : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Error: "Reading local Git history timed out. Try a narrower scene or commit query.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(false, Error: "Git is unavailable or the project folder cannot be read. No files were changed.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<(string Text, bool HasMore)> ReadHistoryPageAsync(
        StreamReader reader, int offset, int maximum, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        long position = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var start = (int)Math.Clamp(offset - position, 0, read);
            var take = Math.Min(read - start, maximum - text.Length);
            if (take > 0) text.Append(buffer, start, take);
            position += read;
        }
        var hasMore = position > (long)offset + maximum;
        if (hasMore && text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return (text.ToString(), hasMore);
    }
}
