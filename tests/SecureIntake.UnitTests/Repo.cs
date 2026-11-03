namespace SecureIntake.UnitTests;

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "SecureIntake.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
