using System.Collections.Concurrent;
using System.Diagnostics;
using CodexUsageHub.Models;

namespace CodexUsageHub.Services;

public sealed class CodexRuntimeManager : IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly ConcurrentDictionary<string, CodexAppServerClient> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new();
    private bool _disposed;

    public CodexRuntimeManager(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<string?> ResolveCodexExecutableAsync(CancellationToken cancellationToken = default)
    {
        var path = await CodexExecutableLocator.FindAsync(_settingsStore.Current.CodexExecutablePath, cancellationToken);
        if (path is not null && !string.Equals(path, _settingsStore.Current.CodexExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            _settingsStore.Current.CodexExecutablePath = path;
            await _settingsStore.SaveAsync(cancellationToken);
        }
        return path;
    }

    public async Task<CodexAccountProfile> AddAccountAsync(string label, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var executable = await ResolveCodexExecutableAsync(cancellationToken)
            ?? throw new FileNotFoundException("Codex CLI was not found. Install Codex CLI or choose codex.exe/codex.cmd in Settings.");

        var profile = new CodexAccountProfile
        {
            Label = string.IsNullOrWhiteSpace(label) ? $"Account {_settingsStore.Current.Accounts.Count + 1}" : label.Trim()
        };
        profile.CodexHome = AppPaths.CreateAccountHome(profile.Id);

        var client = new CodexAppServerClient(executable, profile.CodexHome);
        try
        {
            await client.StartAsync(cancellationToken);
            var login = await client.StartChatGptLoginAsync(cancellationToken);

            Process.Start(new ProcessStartInfo
            {
                FileName = login.AuthUrl,
                UseShellExecute = true
            });

            var completion = await client.WaitForLoginAsync(login.LoginId, TimeSpan.FromMinutes(10), cancellationToken);
            if (!completion.Success)
                throw new InvalidOperationException(completion.Error ?? "ChatGPT login did not complete.");

            var account = await client.ReadAccountAsync(cancellationToken);
            if (!string.Equals(account.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This profile is not signed in with ChatGPT.");

            profile.Email = account.Email;
            profile.PlanType = account.PlanType;

            _settingsStore.Current.Accounts.Add(profile);
            await _settingsStore.SaveAsync(cancellationToken);
            _clients[profile.Id] = client;
            return profile;
        }
        catch
        {
            await client.DisposeAsync();
            TryDeleteProfileDirectory(profile);
            throw;
        }
    }

    public async Task<UsageSnapshot> RefreshAsync(CodexAccountProfile profile, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var gate = _profileGates.GetOrAdd(profile.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            var client = await GetClientAsync(profile, cancellationToken);
            var account = await client.ReadAccountAsync(cancellationToken);

            if (!string.Equals(account.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                return new UsageSnapshot
                {
                    ProfileId = profile.Id,
                    Email = account.Email ?? profile.Email,
                    PlanType = account.PlanType ?? profile.PlanType,
                    Error = account.Type is null ? "Signed out" : $"Unsupported auth: {account.Type}"
                };
            }

            var usage = await client.ReadUsageAsync(cancellationToken);

            var changed = false;
            if (!string.Equals(profile.Email, account.Email, StringComparison.Ordinal))
            {
                profile.Email = account.Email;
                changed = true;
            }
            if (!string.Equals(profile.PlanType, account.PlanType ?? usage.PlanType, StringComparison.Ordinal))
            {
                profile.PlanType = account.PlanType ?? usage.PlanType;
                changed = true;
            }
            if (changed)
                await _settingsStore.SaveAsync(cancellationToken);

            return new UsageSnapshot
            {
                ProfileId = profile.Id,
                Email = profile.Email,
                PlanType = profile.PlanType ?? usage.PlanType,
                AccountId = usage.AccountId,
                Primary = usage.Primary,
                Secondary = usage.Secondary,
                CapturedAt = DateTimeOffset.Now
            };
        }
        catch (Exception ex)
        {
            if (_clients.TryRemove(profile.Id, out var broken))
                await broken.DisposeAsync();

            return new UsageSnapshot
            {
                ProfileId = profile.Id,
                Email = profile.Email,
                PlanType = profile.PlanType,
                Error = CleanError(ex.Message),
                CapturedAt = DateTimeOffset.Now
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAccountAsync(CodexAccountProfile profile, bool deleteProfileData, CancellationToken cancellationToken = default)
    {
        if (_clients.TryRemove(profile.Id, out var client))
            await client.DisposeAsync();

        _settingsStore.Current.Accounts.RemoveAll(a => a.Id == profile.Id);
        await _settingsStore.SaveAsync(cancellationToken);

        if (deleteProfileData)
            TryDeleteProfileDirectory(profile);
    }

    public async Task RestartAllAsync()
    {
        var clients = _clients.ToArray();
        _clients.Clear();
        foreach (var pair in clients)
            await pair.Value.DisposeAsync();
    }

    private async Task<CodexAppServerClient> GetClientAsync(CodexAccountProfile profile, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(profile.Id, out var existing) && existing.IsRunning)
            return existing;

        if (existing is not null)
        {
            _clients.TryRemove(profile.Id, out _);
            await existing.DisposeAsync();
        }

        var executable = await ResolveCodexExecutableAsync(cancellationToken)
            ?? throw new FileNotFoundException("Codex CLI was not found. Use Settings > Browse Codex.");

        var client = new CodexAppServerClient(executable, profile.CodexHome);
        AttachNotifications(profile, client);
        await client.StartAsync(cancellationToken);
        _clients[profile.Id] = client;
        return client;
    }

    private void AttachNotifications(CodexAccountProfile profile, CodexAppServerClient client)
    {
        client.RateLimitsUpdated += (_, _) =>
        {
            if (!_disposed)
                UsageChanged?.Invoke(profile.Id);
        };
    }

    private static string CleanError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Unknown error";

        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 180 ? singleLine : singleLine[..177] + "...";
    }

    private static void TryDeleteProfileDirectory(CodexAccountProfile profile)
    {
        try
        {
            var accountDirectory = Directory.GetParent(profile.CodexHome)?.FullName;
            if (!string.IsNullOrWhiteSpace(accountDirectory) && Directory.Exists(accountDirectory))
                Directory.Delete(accountDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CodexRuntimeManager));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var client in _clients.Values)
        {
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        foreach (var gate in _profileGates.Values)
            gate.Dispose();
    }
}
