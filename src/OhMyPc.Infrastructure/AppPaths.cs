namespace OhMyPc.Infrastructure;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OhMyPc");

    public static string DatabasePath => Path.Combine(DataDirectory, "oh-my-pc.db");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
