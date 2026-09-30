using CodexUsageHub.Services;
using CodexUsageHub.UI;

namespace CodexUsageHub;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppPaths.EnsureCreated();

        using var settingsStore = new SettingsStore();
        using var runtimeManager = new CodexRuntimeManager(settingsStore);
        Application.Run(new MainForm(settingsStore, runtimeManager));
    }
}
