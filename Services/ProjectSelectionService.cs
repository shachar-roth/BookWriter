using System.Text.Json;

namespace IsraeliAuthorStudio.Services;

public sealed class ProjectSelectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private readonly string _defaultProjectPath;
    private ProjectSelectionState? _state;

    public ProjectSelectionService(IWebHostEnvironment environment)
        : this(environment, new ApplicationDataPaths(Path.Combine(environment.ContentRootPath, "App_Data")))
    {
    }

    public ProjectSelectionService(IWebHostEnvironment environment, ApplicationDataPaths applicationData)
    {
        var appDataPath = applicationData.RootPath;
        _statePath = Path.Combine(appDataPath, "current-project.json");
        _defaultProjectPath = Path.Combine(appDataPath, "Story");
    }

    public string CurrentProjectPath
    {
        get
        {
            EnsureStateLoaded();
            return _state!.ProjectPath;
        }
    }

    public string CurrentProjectName =>
        new DirectoryInfo(CurrentProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    public IReadOnlyList<string> RecentProjectPaths
    {
        get
        {
            EnsureStateLoaded();
            return _state!.RecentProjectPaths;
        }
    }

    public async Task SetCurrentProjectPathAsync(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        EnsureStateLoaded();
        _state!.ProjectPath = fullPath;
        _state.RecentProjectPaths.RemoveAll(path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase));
        _state.RecentProjectPaths.Insert(0, fullPath);
        if (_state.RecentProjectPaths.Count > 8)
        {
            _state.RecentProjectPaths.RemoveRange(8, _state.RecentProjectPaths.Count - 8);
        }

        var temporaryPath = $"{_statePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void EnsureStateLoaded()
    {
        if (_state is not null)
        {
            return;
        }

        if (!File.Exists(_statePath))
        {
            _state = new ProjectSelectionState
            {
                ProjectPath = _defaultProjectPath,
                RecentProjectPaths = [_defaultProjectPath]
            };
            return;
        }

        try
        {
            using var stream = File.OpenRead(_statePath);
            _state = JsonSerializer.Deserialize<ProjectSelectionState>(stream, JsonOptions) ?? new ProjectSelectionState();
            _state.ProjectPath = string.IsNullOrWhiteSpace(_state.ProjectPath) ? _defaultProjectPath : Path.GetFullPath(_state.ProjectPath);
            _state.RecentProjectPaths = _state.RecentProjectPaths
                .Where(Directory.Exists)
                .Prepend(_state.ProjectPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            _state = new ProjectSelectionState
            {
                ProjectPath = _defaultProjectPath,
                RecentProjectPaths = [_defaultProjectPath]
            };
        }
    }

    private sealed class ProjectSelectionState
    {
        public string ProjectPath { get; set; } = "";
        public List<string> RecentProjectPaths { get; set; } = [];
    }
}
