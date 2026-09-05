using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IsraeliAuthorStudio.Services;

public sealed class FolderPickerService
{
    public async Task<string?> PickFolderAsync(string prompt)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await PickWindowsFolderAsync(prompt);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await PickMacFolderAsync(prompt);
        }

        return await PickLinuxFolderAsync(prompt);
    }

    private static async Task<string?> PickWindowsFolderAsync(string prompt)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"IsraeliAuthorStudio-folder-{Guid.NewGuid():N}.txt");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"IsraeliAuthorStudio-folder-{Guid.NewGuid():N}.vbs");
        var escapedPrompt = prompt.Replace("\"", "\"\"", StringComparison.Ordinal);
        var escapedOutputPath = outputPath.Replace("\"", "\"\"", StringComparison.Ordinal);

        var script = string.Join(Environment.NewLine, [
            "Set shell = CreateObject(\"Shell.Application\")",
            $"Set folder = shell.BrowseForFolder(0, \"{escapedPrompt}\", 0, 0)",
            "If Not folder Is Nothing Then",
            "  Set fso = CreateObject(\"Scripting.FileSystemObject\")",
            $"  Set file = fso.CreateTextFile(\"{escapedOutputPath}\", True, True)",
            "  file.Write folder.Self.Path",
            "  file.Close",
            "End If"
        ]);

        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "wscript.exe",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = true
            });

            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync();

            if (!File.Exists(outputPath))
            {
                return null;
            }

            var selectedPath = (await File.ReadAllTextAsync(outputPath)).Trim();
            return string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath;
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(outputPath);
        }
    }

    private static async Task<string?> PickMacFolderAsync(string prompt)
    {
        var escapedPrompt = prompt.Replace("\"", "\\\"", StringComparison.Ordinal);
        return await RunProcessForSingleLineAsync(
            "/usr/bin/osascript",
            $"-e \"POSIX path of (choose folder with prompt \\\"{escapedPrompt}\\\")\"");
    }

    private static async Task<string?> PickLinuxFolderAsync(string prompt)
    {
        var escapedPrompt = prompt.Replace("\"", "\\\"", StringComparison.Ordinal);
        return await RunProcessForSingleLineAsync("zenity", $"--file-selection --directory --title=\"{escapedPrompt}\"");
    }

    private static async Task<string?> RunProcessForSingleLineAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return null;
            }

            var selectedPath = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary picker files are best-effort cleanup only.
        }
    }
}
