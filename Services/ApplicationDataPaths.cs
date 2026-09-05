namespace IsraeliAuthorStudio.Services;

public sealed class ApplicationDataPaths
{
    public ApplicationDataPaths(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public static ApplicationDataPaths Create(bool desktopMode, string contentRootPath)
    {
        if (!desktopMode) return new ApplicationDataPaths(Path.Combine(contentRootPath, "App_Data"));

        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "IsraeliAuthorStudio")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IsraeliAuthorStudio");
        return new ApplicationDataPaths(root);
    }
}
