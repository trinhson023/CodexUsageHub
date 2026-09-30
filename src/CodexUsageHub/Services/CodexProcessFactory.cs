using System.Diagnostics;

namespace CodexUsageHub.Services;

public static class CodexProcessFactory
{
    public static ProcessStartInfo CreateAppServer(string codexExecutablePath, string codexHome)
    {
        ProcessStartInfo info;
        var extension = Path.GetExtension(codexExecutablePath);

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info = new ProcessStartInfo
            {
                FileName = comspec,
                Arguments = $"/d /s /c \"\"{codexExecutablePath}\" app-server\"",
            };
        }
        else
        {
            info = new ProcessStartInfo
            {
                FileName = codexExecutablePath,
                Arguments = "app-server"
            };
        }

        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.Environment["CODEX_HOME"] = codexHome;
        info.Environment.Remove("OPENAI_API_KEY");
        info.Environment.Remove("CODEX_API_KEY");
        return info;
    }
}
