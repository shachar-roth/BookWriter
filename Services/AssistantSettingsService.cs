using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IsraeliAuthorStudio.Models;

namespace IsraeliAuthorStudio.Services;

public interface ICredentialStore
{
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);
    Task SetAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

public sealed class PlatformCredentialStore : ICredentialStore
{
    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Task.FromResult(WindowsCredentialApi.Read(name));
        }

        return RunSecurityAsync(["find-generic-password", "-s", name, "-w"], cancellationToken, allowFailure: true);
    }

    public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsCredentialApi.Write(name, secret);
            return Task.CompletedTask;
        }

        return RunSecurityAsync(["add-generic-password", "-U", "-s", name, "-a", Environment.UserName, "-w", secret], cancellationToken);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsCredentialApi.Delete(name);
            return Task.CompletedTask;
        }

        return RunSecurityAsync(["delete-generic-password", "-s", name], cancellationToken, allowFailure: true);
    }

    private static async Task<string?> RunSecurityAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowFailure = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (allowFailure) return null;
            throw new PlatformNotSupportedException("Secure credential storage is supported on Windows and macOS.");
        }

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException(error.Trim());
        return process.ExitCode == 0 ? output.Trim() : null;
    }

    private static class WindowsCredentialApi
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        public static void Write(string name, string secret)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var pointer = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = name,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = pointer,
                    Persist = CredPersistLocalMachine,
                    UserName = Environment.UserName
                };
                if (!CredWrite(ref credential, 0)) throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        public static string? Read(string name)
        {
            if (!CredRead(name, CredTypeGeneric, 0, out var pointer)) return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                return credential.CredentialBlob == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static void Delete(string name) => _ = CredDelete(name, CredTypeGeneric, 0);
    }
}

public sealed class AssistantSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly ICredentialStore _credentials;

    public AssistantSettingsService(IWebHostEnvironment environment, ICredentialStore credentials)
        : this(environment, credentials, new ApplicationDataPaths(Path.Combine(environment.ContentRootPath, "App_Data")))
    {
    }

    public AssistantSettingsService(
        IWebHostEnvironment environment,
        ICredentialStore credentials,
        ApplicationDataPaths applicationData)
    {
        _settingsPath = Path.Combine(applicationData.RootPath, "assistant-settings.json");
        _credentials = credentials;
    }

    public async Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath)) return new AssistantSettings();
        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AssistantSettings>(stream, JsonOptions, cancellationToken) ?? new AssistantSettings();
        }
        catch (JsonException)
        {
            return new AssistantSettings();
        }
    }

    public async Task SaveAsync(AssistantSettings settings, string? apiKey, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = $"{_settingsPath}.tmp-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
        if (!string.IsNullOrWhiteSpace(apiKey)) await _credentials.SetAsync(settings.Provider.CredentialName, apiKey, cancellationToken);
    }

    public Task<string?> GetApiKeyAsync(ProviderProfile profile, CancellationToken cancellationToken = default) =>
        _credentials.GetAsync(profile.CredentialName, cancellationToken);
}
