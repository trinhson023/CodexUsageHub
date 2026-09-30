using System.Diagnostics;

namespace CodexUsageHub.Services;

public static class CodexExecutableLocator
{
    public static async Task<string?> FindAsync(string? configuredPath, CancellationToken cancellationToken = default)
    {
        if (IsUsable(configuredPath))
            return Path.GetFullPath(configuredPath!);

        foreach (var candidate in GetKnownCandidates())
        {
            if (IsUsable(candidate))
                return candidate;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "codex",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;

            var paths = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var preferred = paths.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? paths.FirstOrDefault(p => p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                ?? paths.FirstOrDefault();

            return IsUsable(preferred) ? preferred : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetKnownCandidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(appData, "npm", "codex.cmd");
        yield return Path.Combine(appData, "npm", "codex.exe");
        yield return Path.Combine(userProfile, ".local", "bin", "codex.exe");
        yield return Path.Combine(userProfile, ".codex", "bin", "codex.exe");

        foreach (var candidate in FindVersionedBinaries(
                     Path.Combine(localAppData, "OpenAI", "Codex", "bin"),
                     "*",
                     Path.Combine("codex.exe")))
            yield return candidate;

        foreach (var extensionRoot in new[]
                 {
                     Path.Combine(userProfile, ".vscode", "extensions"),
                     Path.Combine(userProfile, ".vscode-insiders", "extensions"),
                     Path.Combine(userProfile, ".cursor", "extensions")
                 })
        {
            foreach (var candidate in FindVersionedBinaries(
                         extensionRoot,
                         "openai.chatgpt-*-win32-x64",
                         Path.Combine("bin", "windows-x86_64", "codex.exe")))
                yield return candidate;
        }
    }

    private static IEnumerable<string> FindVersionedBinaries(string root, string directoryPattern, string relativeBinaryPath)
    {
        if (!Directory.Exists(root))
            yield break;

        DirectoryInfo[] directories;
        try
        {
            directories = new DirectoryInfo(root)
                .GetDirectories(directoryPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory.FullName, relativeBinaryPath);
            if (IsUsable(candidate))
                yield return candidate;
        }
    }

    public static bool IsUsable(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
