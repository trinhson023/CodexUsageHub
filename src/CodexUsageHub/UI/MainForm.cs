using CodexUsageHub.Models;
using CodexUsageHub.Services;

namespace CodexUsageHub.UI;

public sealed class MainForm : Form
{
    private readonly SettingsStore _settingsStore;
    private readonly CodexRuntimeManager _runtimeManager;
    private readonly DataGridView _grid = new();
    private readonly ToolStripButton _addButton = new("+ Add account");
    private readonly ToolStripButton _refreshButton = new("Refresh");
    private readonly ToolStripButton _removeButton = new("Remove");
    private readonly ToolStripButton _overlayButton = new("Overlay");
    private readonly ToolStripButton _browseCodexButton = new("Browse Codex");
    private readonly ToolStripLabel _codexPathLabel = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Ready");
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, UsageSnapshot> _snapshots = new();
    private readonly CancellationTokenSource _lifetime = new();
    private OverlayForm? _overlay;
    private bool _exitRequested;

    public MainForm(SettingsStore settingsStore, CodexRuntimeManager runtimeManager)
    {
        _settingsStore = settingsStore;
        _runtimeManager = runtimeManager;

        Text = "Codex Usage Hub";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 420);
        Size = new Size(1100, 520);

        BuildUi();
        BuildTray();
        WireEvents();

        _timer.Interval = Math.Max(15, _settingsStore.Current.RefreshSeconds) * 1000;
        _timer.Start();

        Shown += async (_, _) =>
        {
            await ResolveCodexPathAsync();
            if (_settingsStore.Current.OverlayEnabled)
                ShowOverlay();
            await RefreshAllAsync();
        };
    }

    private void BuildUi()
    {
        var toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        toolStrip.Items.AddRange([
            _addButton,
            _refreshButton,
            _removeButton,
            new ToolStripSeparator(),
            _overlayButton,
            new ToolStripSeparator(),
            _browseCodexButton,
            new ToolStripSeparator(),
            _codexPathLabel
        ]);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = SystemColors.Window;

        _grid.Columns.Add("account", "Account");
        _grid.Columns.Add("email", "Email");
        _grid.Columns.Add("plan", "Plan");
        _grid.Columns.Add("primary", "Limit 1");
        _grid.Columns.Add("secondary", "Limit 2");
        _grid.Columns.Add("updated", "Updated / Status");

        _grid.Columns[0].FillWeight = 90;
        _grid.Columns[1].FillWeight = 145;
        _grid.Columns[2].FillWeight = 65;
        _grid.Columns[3].FillWeight = 135;
        _grid.Columns[4].FillWeight = 135;
        _grid.Columns[5].FillWeight = 145;

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(statusStrip);
        Controls.Add(toolStrip);
        RenderGrid();
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshAllAsync());
        menu.Items.Add("Toggle overlay", null, (_, _) => ToggleOverlay());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.Text = "Codex Usage Hub";
        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Visible = true;
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void WireEvents()
    {
        _addButton.Click += async (_, _) => await AddAccountAsync();
        _refreshButton.Click += async (_, _) => await RefreshAllAsync();
        _removeButton.Click += async (_, _) => await RemoveSelectedAsync();
        _overlayButton.Click += (_, _) => ToggleOverlay();
        _browseCodexButton.Click += async (_, _) => await BrowseCodexAsync();
        _timer.Tick += async (_, _) => await RefreshAllAsync();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                _trayIcon.ShowBalloonTip(1200, "Codex Usage Hub", "Still running in the system tray.", ToolTipIcon.Info);
            }
        };

        FormClosing += (_, e) =>
        {
            if (!_exitRequested)
            {
                e.Cancel = true;
                Hide();
                WindowState = FormWindowState.Minimized;
            }
        };

        FormClosed += (_, _) =>
        {
            _lifetime.Cancel();
            _timer.Stop();
            _trayIcon.Visible = false;
            _overlay?.Close();
            _refreshGate.Dispose();
            _lifetime.Dispose();
        };
    }

    private async Task AddAccountAsync()
    {
        using var prompt = new PromptDialog(
            "Add Codex account",
            "Display name for this account:",
            $"Account {_settingsStore.Current.Accounts.Count + 1}");

        if (prompt.ShowDialog(this) != DialogResult.OK)
            return;

        SetBusy(true, "Opening ChatGPT login in your browser...");
        try
        {
            var profile = await _runtimeManager.AddAccountAsync(prompt.Value, _lifetime.Token);
            _statusLabel.Text = $"Added {profile.Email ?? profile.Label}";
            RenderGrid();
            await RefreshAllAsync();
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Login cancelled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not add account", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "Add account failed";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        var profile = GetSelectedProfile();
        if (profile is null)
            return;

        var result = MessageBox.Show(
            this,
            $"Remove '{profile.Label}' and delete its isolated Codex login data from this app?",
            "Remove account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        SetBusy(true, "Removing account...");
        try
        {
            await _runtimeManager.RemoveAccountAsync(profile, deleteProfileData: true, _lifetime.Token);
            _snapshots.Remove(profile.Id);
            RenderGrid();
            UpdateOverlay();
            _statusLabel.Text = "Account removed";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshAllAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
            return;

        try
        {
            if (_settingsStore.Current.Accounts.Count == 0)
            {
                _statusLabel.Text = "Add a Codex account to begin";
                RenderGrid();
                UpdateOverlay();
                return;
            }

            _refreshButton.Enabled = false;
            _statusLabel.Text = "Refreshing usage...";

            var accounts = _settingsStore.Current.Accounts.ToArray();
            var tasks = accounts.Select(a => _runtimeManager.RefreshAsync(a, _lifetime.Token)).ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var snapshot in results)
                _snapshots[snapshot.ProfileId] = snapshot;

            RenderGrid();
            UpdateOverlay();
            UpdateTrayText();
            _statusLabel.Text = $"Updated {DateTime.Now:HH:mm:ss} · auto refresh {_settingsStore.Current.RefreshSeconds}s";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            _refreshButton.Enabled = true;
            _refreshGate.Release();
        }
    }

    private void RenderGrid()
    {
        var selectedId = GetSelectedProfile()?.Id;
        _grid.Rows.Clear();

        foreach (var profile in _settingsStore.Current.Accounts)
        {
            _snapshots.TryGetValue(profile.Id, out var snapshot);

            var rowIndex = _grid.Rows.Add(
                profile.Label,
                snapshot?.Email ?? profile.Email ?? "—",
                snapshot?.PlanType ?? profile.PlanType ?? "—",
                FormatWindow(snapshot?.Primary),
                FormatWindow(snapshot?.Secondary),
                snapshot is null
                    ? "Not refreshed"
                    : snapshot.IsSuccess
                        ? snapshot.CapturedAt.LocalDateTime.ToString("HH:mm:ss")
                        : snapshot.Error ?? "Unavailable");

            var row = _grid.Rows[rowIndex];
            row.Tag = profile.Id;
            if (!string.IsNullOrWhiteSpace(snapshot?.Error))
                row.Cells[5].ToolTipText = snapshot.Error;

            if (profile.Id == selectedId)
                row.Selected = true;
        }
    }

    private static string FormatWindow(RateLimitWindowSnapshot? window)
    {
        if (window is null)
            return "—";

        var name = OverlayForm.WindowName(window);
        var reset = window.ResetsAt is DateTimeOffset time
            ? $" · reset {time:HH:mm dd/MM}"
            : string.Empty;
        return $"{name}: {window.RemainingPercent}% left{reset}";
    }

    private CodexAccountProfile? GetSelectedProfile()
    {
        if (_grid.SelectedRows.Count == 0)
            return null;

        var id = _grid.SelectedRows[0].Tag as string;
        return _settingsStore.Current.Accounts.FirstOrDefault(a => a.Id == id);
    }

    private async Task ResolveCodexPathAsync()
    {
        var path = await _runtimeManager.ResolveCodexExecutableAsync(_lifetime.Token);
        _codexPathLabel.Text = path is null ? "Codex: not found" : $"Codex: {Path.GetFileName(path)}";
        _codexPathLabel.ToolTipText = path ?? "Use Browse Codex to select codex.exe or codex.cmd";
    }

    private async Task BrowseCodexAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Codex CLI executable",
            Filter = "Codex CLI (codex.exe;codex.cmd)|codex.exe;codex.cmd|Executable or command files (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _settingsStore.Current.CodexExecutablePath = dialog.FileName;
        await _settingsStore.SaveAsync(_lifetime.Token);
        await _runtimeManager.RestartAllAsync();
        await ResolveCodexPathAsync();
        await RefreshAllAsync();
    }

    private void ToggleOverlay()
    {
        if (_overlay is { Visible: true })
        {
            _overlay.Hide();
            _settingsStore.Current.OverlayEnabled = false;
            _ = _settingsStore.SaveAsync();
            return;
        }

        ShowOverlay();
    }

    private void ShowOverlay()
    {
        if (_overlay is null || _overlay.IsDisposed)
        {
            _overlay = new OverlayForm();
            _overlay.Location = new Point(_settingsStore.Current.OverlayX, _settingsStore.Current.OverlayY);
            _overlay.OverlayMoved += async (_, _) =>
            {
                if (_overlay is null)
                    return;
                _settingsStore.Current.OverlayX = _overlay.Left;
                _settingsStore.Current.OverlayY = _overlay.Top;
                await _settingsStore.SaveAsync();
            };
        }

        UpdateOverlay();
        _overlay.Show();
        _settingsStore.Current.OverlayEnabled = true;
        _ = _settingsStore.SaveAsync();
    }

    private void UpdateOverlay()
    {
        if (_overlay is null || _overlay.IsDisposed)
            return;

        var items = _settingsStore.Current.Accounts
            .Where(a => _snapshots.ContainsKey(a.Id))
            .Select(a => (a, _snapshots[a.Id]))
            .ToList();
        _overlay.UpdateSnapshots(items);
    }

    private void UpdateTrayText()
    {
        foreach (var profile in _settingsStore.Current.Accounts)
        {
            if (!_snapshots.TryGetValue(profile.Id, out var snapshot))
                continue;

            var windows = new[] { snapshot.Primary, snapshot.Secondary }
                .Where(w => w is not null)
                .Select(w => $"{OverlayForm.WindowName(w!)} {w!.RemainingPercent}%")
                .ToArray();

            var text = windows.Length == 0
                ? $"{profile.Label}: {snapshot.Error ?? "No usage data"}"
                : $"{profile.Label}: {string.Join(" | ", windows)}";

            _trayIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
            return;
        }

        _trayIcon.Text = "Codex Usage Hub";
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _addButton.Enabled = !busy;
        _removeButton.Enabled = !busy;
        _browseCodexButton.Enabled = !busy;
        if (status is not null)
            _statusLabel.Text = status;
        UseWaitCursor = busy;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;
        Close();
    }
}
