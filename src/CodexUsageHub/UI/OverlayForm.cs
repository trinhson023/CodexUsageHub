using CodexUsageHub.Models;

namespace CodexUsageHub.UI;

public sealed class OverlayForm : Form
{
    private readonly TableLayoutPanel _layout = new();
    private Point _dragStart;
    private bool _dragging;

    public event EventHandler? OverlayMoved;

    public OverlayForm()
    {
        Text = "Codex Usage Overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = 0.92;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;

        _layout.AutoSize = true;
        _layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _layout.ColumnCount = 1;
        Controls.Add(_layout);

        MouseDown += BeginDrag;
        MouseMove += ContinueDrag;
        MouseUp += EndDrag;
        _layout.MouseDown += BeginDrag;
        _layout.MouseMove += ContinueDrag;
        _layout.MouseUp += EndDrag;
    }

    protected override bool ShowWithoutActivation => true;

    public void UpdateSnapshots(IReadOnlyList<(CodexAccountProfile Profile, UsageSnapshot Snapshot)> items)
    {
        _layout.SuspendLayout();
        _layout.Controls.Clear();
        _layout.RowStyles.Clear();

        var title = CreateLabel("Codex Usage", FontStyle.Bold);
        _layout.Controls.Add(title);

        foreach (var (profile, snapshot) in items)
        {
            var account = CreateLabel(BuildAccountLine(profile, snapshot), FontStyle.Bold);
            var limits = CreateLabel(BuildLimitLine(snapshot), FontStyle.Regular);
            _layout.Controls.Add(account);
            _layout.Controls.Add(limits);
        }

        if (items.Count == 0)
            _layout.Controls.Add(CreateLabel("No accounts", FontStyle.Regular));

        _layout.ResumeLayout(true);
    }

    private Label CreateLabel(string text, FontStyle style)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = ForeColor,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 2, 2, 4),
            Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, style)
        };
        label.MouseDown += BeginDrag;
        label.MouseMove += ContinueDrag;
        label.MouseUp += EndDrag;
        return label;
    }

    private static string BuildAccountLine(CodexAccountProfile profile, UsageSnapshot snapshot)
    {
        var plan = snapshot.PlanType ?? profile.PlanType ?? "?";
        return $"{profile.Label} · {plan}";
    }

    private static string BuildLimitLine(UsageSnapshot snapshot)
    {
        if (!snapshot.IsSuccess)
            return snapshot.Error ?? "Unavailable";

        var windows = new[] { snapshot.Primary, snapshot.Secondary }
            .Where(w => w is not null)
            .Select(w => $"{WindowName(w!)} {w!.RemainingPercent}%")
            .ToArray();

        return windows.Length == 0 ? "No rate-limit windows" : string.Join("  |  ", windows);
    }

    internal static string WindowName(RateLimitWindowSnapshot window)
    {
        var mins = window.WindowDurationMins;
        if (mins is null)
            return "Limit";
        if (mins == 300)
            return "5h";
        if (mins == 10080)
            return "7d";
        if (mins % 1440 == 0)
            return $"{mins / 1440}d";
        if (mins % 60 == 0)
            return $"{mins / 60}h";
        return $"{mins}m";
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        _dragging = true;
        _dragStart = e.Location;
    }

    private void ContinueDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var screen = (sender as Control ?? this).PointToScreen(e.Location);
        Location = new Point(screen.X - _dragStart.X, screen.Y - _dragStart.Y);
    }

    private void EndDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        OverlayMoved?.Invoke(this, EventArgs.Empty);
    }
}
