namespace DataCenterSaveEditor.Core;

public static class SavePairLocator
{
    public static string DefaultSaveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "WASEKU", "Data Center", "saves");

    public static IReadOnlyList<SavePair> Discover(string? directory = null)
    {
        string path = directory ?? DefaultSaveDirectory;
        if (!Directory.Exists(path))
        {
            return [];
        }

        return Directory.EnumerateFiles(path, "*.save", SearchOption.TopDirectoryOnly)
            .Select(SavePair.FromSavePath)
            .Where(pair => File.Exists(pair.MetaPath))
            .OrderByDescending(pair => File.GetLastWriteTimeUtc(pair.SavePath))
            .ToArray();
    }
}
