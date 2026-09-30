using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodexUsageHub.Models;

namespace CodexUsageHub.Services;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly string _codexExecutablePath;
    private readonly string _codexHome;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LoginCompletion>> _loginWaiters = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _stderrTail = [];
    private Process? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private int _nextRequestId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool IsRunning => _process is { HasExited: false };

    public CodexAppServerClient(string codexExecutablePath, string codexHome)
    {
        _codexExecutablePath = codexExecutablePath;
        _codexHome = codexHome;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        Directory.CreateDirectory(_codexHome);

        _process = new Process
        {
            StartInfo = CodexProcessFactory.CreateAppServer(_codexExecutablePath, _codexHome),
            EnableRaisingEvents = true
        };

        if (!_process.Start())
            throw new InvalidOperationException("Could not start Codex app-server.");

        _readerTask = Task.Run(() => ReaderLoopAsync(_lifetime.Token));
        _stderrTask = Task.Run(() => StderrLoopAsync(_lifetime.Token));

        try
        {
            await RequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "codex_usage_hub",
                        title = "Codex Usage Hub",
                        version = "1.0.0"
                    },
                    capabilities = new
                    {
                        experimentalApi = false
                    }
                },
                cancellationToken);

            await SendNotificationAsync("initialized", cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<AccountInfo> ReadAccountAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "account/read",
            new { refreshToken = false },
            cancellationToken);

        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
            return new AccountInfo(null, null, null);

        var type = account.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var email = account.TryGetProperty("email", out var emailElement) && emailElement.ValueKind != JsonValueKind.Null
            ? emailElement.GetString()
            : null;
        var plan = account.TryGetProperty("planType", out var planElement) && planElement.ValueKind != JsonValueKind.Null
            ? planElement.GetString()
            : null;

        return new AccountInfo(type, email, plan);
    }

    public async Task<UsageRead> ReadUsageAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("account/rateLimits/read", null, cancellationToken, includeParams: false);

        string? accountId = null;
        if (result.TryGetProperty("accountId", out var accountIdElement) && accountIdElement.ValueKind != JsonValueKind.Null)
            accountId = accountIdElement.GetString();

        JsonElement snapshot;
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object &&
            byId.TryGetProperty("codex", out var codexSnapshot))
        {
            snapshot = codexSnapshot;
        }
        else if (result.TryGetProperty("rateLimits", out var fallback))
        {
            snapshot = fallback;
        }
        else
        {
            throw new InvalidOperationException("Codex returned no rate limit snapshot.");
        }

        var planType = snapshot.TryGetProperty("planType", out var plan) && plan.ValueKind != JsonValueKind.Null
            ? plan.GetString()
            : null;

        return new UsageRead(
            accountId,
            planType,
            ParseWindow(snapshot, "primary"),
            ParseWindow(snapshot, "secondary"));
    }

    public async Task<LoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "account/login/start",
            new { type = "chatgpt" },
            cancellationToken);

        var loginId = result.GetProperty("loginId").GetString()
            ?? throw new InvalidOperationException("Codex did not return a loginId.");
        var authUrl = result.GetProperty("authUrl").GetString()
            ?? throw new InvalidOperationException("Codex did not return an authUrl.");

        return new LoginStart(loginId, authUrl);
    }

    public async Task<LoginCompletion> WaitForLoginAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<LoginCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_loginWaiters.TryAdd(loginId, tcs))
            throw new InvalidOperationException("A login waiter already exists for this login.");

        try
        {
            return await tcs.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            _loginWaiters.TryRemove(loginId, out _);
        }
    }

    private static RateLimitWindowSnapshot? ParseWindow(JsonElement snapshot, string propertyName)
    {
        if (!snapshot.TryGetProperty(propertyName, out var window) || window.ValueKind == JsonValueKind.Null)
            return null;

        if (!window.TryGetProperty("usedPercent", out var usedPercentElement))
            return null;

        var usedPercent = usedPercentElement.GetInt32();
        long? duration = null;
        long? resetsAt = null;

        if (window.TryGetProperty("windowDurationMins", out var durationElement) && durationElement.ValueKind != JsonValueKind.Null)
            duration = durationElement.GetInt64();

        if (window.TryGetProperty("resetsAt", out var resetsAtElement) && resetsAtElement.ValueKind != JsonValueKind.Null)
            resetsAt = resetsAtElement.GetInt64();

        return new RateLimitWindowSnapshot
        {
            UsedPercent = usedPercent,
            WindowDurationMins = duration,
            ResetsAtUnixSeconds = resetsAt
        };
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        bool includeParams = true)
    {
        if (!IsRunning || _process is null)
            throw new InvalidOperationException("Codex app-server is not running.");

        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
            throw new InvalidOperationException("Could not register Codex request.");

        try
        {
            var payload = includeParams
                ? JsonSerializer.Serialize(new { id, method, @params = parameters }, JsonOptions)
                : JsonSerializer.Serialize(new { id, method }, JsonOptions);

            await WriteLineAsync(payload, cancellationToken);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { method }, JsonOptions);
        return WriteLineAsync(payload, cancellationToken);
    }

    private async Task WriteLineAsync(string payload, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException(BuildProcessFailureMessage());

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                HandleMessage(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailAllPending(ex);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
                FailAllPending(new InvalidOperationException(BuildProcessFailureMessage()));
        }
    }

    private async Task StderrLoopAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                lock (_stderrTail)
                {
                    _stderrTail.Add(line);
                    if (_stderrTail.Count > 20)
                        _stderrTail.RemoveAt(0);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void HandleMessage(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            {
                var id = idElement.GetInt32();
                if (!_pending.TryGetValue(id, out var tcs))
                    return;

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : error.GetRawText();
                    tcs.TrySetException(new InvalidOperationException(message ?? "Codex request failed."));
                    return;
                }

                if (root.TryGetProperty("result", out var result))
                    tcs.TrySetResult(result.Clone());
                else
                    tcs.TrySetResult(default);

                return;
            }

            if (!root.TryGetProperty("method", out var methodElement))
                return;

            if (!string.Equals(methodElement.GetString(), "account/login/completed", StringComparison.Ordinal))
                return;

            if (!root.TryGetProperty("params", out var parameters))
                return;

            var loginId = parameters.TryGetProperty("loginId", out var loginIdElement) && loginIdElement.ValueKind != JsonValueKind.Null
                ? loginIdElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(loginId))
                return;

            var success = parameters.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
            var loginError = parameters.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null
                ? errorElement.GetString()
                : null;

            if (_loginWaiters.TryGetValue(loginId, out var waiter))
                waiter.TrySetResult(new LoginCompletion(success, loginError));
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pending in _pending.Values)
            pending.TrySetException(exception);
    }

    private string BuildProcessFailureMessage()
    {
        string details;
        lock (_stderrTail)
            details = string.Join(Environment.NewLine, _stderrTail.TakeLast(5));

        return string.IsNullOrWhiteSpace(details)
            ? "Codex app-server stopped unexpectedly."
            : $"Codex app-server stopped unexpectedly. {details}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return;

        _lifetime.Cancel();

        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            if (_readerTask is not null)
                await _readerTask;
        }
        catch
        {
        }

        try
        {
            if (_stderrTask is not null)
                await _stderrTask;
        }
        catch
        {
        }

        _process?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}

public sealed record AccountInfo(string? Type, string? Email, string? PlanType);
public sealed record UsageRead(string? AccountId, string? PlanType, RateLimitWindowSnapshot? Primary, RateLimitWindowSnapshot? Secondary);
public sealed record LoginStart(string LoginId, string AuthUrl);
public sealed record LoginCompletion(bool Success, string? Error);
