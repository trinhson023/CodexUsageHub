namespace CodexUsageHub.Services;

public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageHub");

    public static string AccountsDirectory { get; } = Path.Combine(RootDirectory, "accounts");
    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AccountsDirectory);
    }

    public static string CreateAccountHome(string profileId)
    {
        var path = Path.Combine(AccountsDirectory, profileId, ".codex");
        Directory.CreateDirectory(path);

        // Keep each ChatGPT login truly isolated inside this CODEX_HOME.
        // Codex officially supports file-backed CLI auth via this setting.
        var configPath = Path.Combine(path, "config.toml");
        if (!File.Exists(configPath))
            File.WriteAllText(configPath, "cli_auth_credentials_store = \"file\"" + Environment.NewLine);

        return path;
    }
}
