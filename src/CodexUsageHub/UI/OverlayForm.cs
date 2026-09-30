using System.Drawing.Drawing2D;
using CodexUsageHub.Models;

namespace CodexUsageHub.UI;

public sealed class OverlayForm : Form
{
    private const int OverlayWidth = 370;
    private const int OuterPadding = 12;
    private const int HeaderHeight = 54;
    private const int CardHeight = 112;
    private const int CardGap = 8;
    private const int CornerRadius = 18;

    private static readonly Color Surface = Color.FromArgb(20, 23, 29);
    private static readonly Color Card = Color.FromArgb(31, 35, 43);
    private static readonly Color CardBorder = Color.FromArgb(48, 54, 65);
    private static readonly Color Track = Color.FromArgb(54, 60, 72);
    private static readonly Color TextPrimary = Color.FromArgb(245, 247, 250);
    private static readonly Color TextSecondary = Color.FromArgb(160, 168, 181);
    private static readonly Color Accent = Color.FromArgb(111, 126, 255);
    private static readonly Color Healthy = Color.FromArgb(79, 211, 157);
    private static readonly Color Warning = Color.FromArgb(245, 184, 78);
    private static readonly Color Critical = Color.FromArgb(245, 103, 116);

    private readonly List<OverlayItem> _items = [];
    private readonly Dictionary<string, AnimatedAccount> _animation = new();
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private readonly ContextMenuStrip _menu = new();
    private readonly Font _titleFont = new("Segoe UI Semibold", 10.5f, FontStyle.Bold);
    private readonly Font _accountFont = new("Segoe UI Semibold", 9.5f, FontStyle.Bold);
    private readonly Font _bodyFont = new("Segoe UI", 8.5f, FontStyle.Regular);
    private readonly Font _smallFont = new("Segoe UI", 7.5f, FontStyle.Regular);
    private readonly Font _smallBoldFont = new("Segoe UI Semibold", 7.5f, FontStyle.Bold);

    private Point _dragOriginScreen;
    private Point _dragOriginForm;
    private bool _dragging;
    private float _globalFlash;

    public event EventHandler? OverlayMoved;
    public event EventHandler? RefreshRequested;
    public event EventHandler? HideRequested;

    public OverlayForm()
    {
        Text = "Codex Usage Overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Surface;
        ForeColor = TextPrimary;
        Opacity = 0;
        ClientSize = new Size(OverlayWidth, HeaderHeight + 78);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _menu.Items.Add("Refresh now", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("Hide overlay", null, (_, _) => HideRequested?.Invoke(this, EventArgs.Empty));
        ContextMenuStrip = _menu;

        _animationTimer.Tick += (_, _) => AnimateFrame();

        MouseDown += BeginDrag;
        MouseMove += ContinueDrag;
        MouseUp += EndDrag;

        Shown += (_, _) =>
        {
            Opacity = 0;
            _animationTimer.Start();
        };

        Resize += (_, _) => UpdateRoundedRegion();
        UpdateRoundedRegion();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int CsDropShadow = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    public void UpdateSnapshots(IReadOnlyList<(CodexAccountProfile Profile, UsageSnapshot Snapshot)> items)
    {
        _items.Clear();
        foreach (var (profile, snapshot) in items)
            _items.Add(new OverlayItem(profile, snapshot));

        var liveIds = new HashSet<string>(_items.Select(i => i.Profile.Id));
        foreach (var removedId in _animation.Keys.Where(id => !liveIds.Contains(id)).ToArray())
            _animation.Remove(removedId);

        foreach (var item in _items)
        {
            var primaryTarget = item.Snapshot.Primary?.RemainingPercent ?? 0;
            var secondaryTarget = item.Snapshot.Secondary?.RemainingPercent ?? 0;

            if (!_animation.TryGetValue(item.Profile.Id, out var state))
            {
                state = new AnimatedAccount
                {
                    Primary = primaryTarget,
                    Secondary = secondaryTarget,
                    PrimaryTarget = primaryTarget,
                    SecondaryTarget = secondaryTarget
                };
                _animation[item.Profile.Id] = state;
                continue;
            }

            if (Math.Abs(state.PrimaryTarget - primaryTarget) > 0.01f ||
                Math.Abs(state.SecondaryTarget - secondaryTarget) > 0.01f)
            {
                state.Flash = 1f;
            }

            state.PrimaryTarget = primaryTarget;
            state.SecondaryTarget = secondaryTarget;
        }

        ResizeForContent();
        _animationTimer.Start();
        Invalidate();
    }

    public void PulseLive()
    {
        _globalFlash = 1f;
        _animationTimer.Start();
        Invalidate();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var backgroundPath = RoundedRect(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1), CornerRadius))
        using (var backgroundBrush = new SolidBrush(Surface))
        using (var borderPen = new Pen(Color.FromArgb(70, 78, 92), 1f))
        {
            g.FillPath(backgroundBrush, backgroundPath);
            g.DrawPath(borderPen, backgroundPath);
        }

        DrawHeader(g);

        if (_items.Count == 0)
        {
            DrawEmptyState(g);
            return;
        }

        var y = HeaderHeight + 4;
        foreach (var item in _items)
        {
            DrawAccountCard(g, item, new RectangleF(OuterPadding, y, ClientSize.Width - OuterPadding * 2, CardHeight));
            y += CardHeight + CardGap;
        }
    }

    private void DrawHeader(Graphics g)
    {
        var dotColor = Blend(Healthy, Color.White, Math.Min(0.35f, _globalFlash * 0.35f));
        using (var dotBrush = new SolidBrush(dotColor))
            g.FillEllipse(dotBrush, 17, 18, 8, 8);

        using (var titleBrush = new SolidBrush(TextPrimary))
            g.DrawString("CODEX USAGE", _titleFont, titleBrush, 32, 12);

        using (var subBrush = new SolidBrush(TextSecondary))
            g.DrawString("live events · 60s safety refresh", _smallFont, subBrush, 32, 31);

        var badgeRect = new RectangleF(ClientSize.Width - 70, 13, 52, 24);
        using (var badgePath = RoundedRect(badgeRect, 12))
        using (var badgeBrush = new SolidBrush(Color.FromArgb(36, 77, 64)))
        using (var liveBrush = new SolidBrush(Color.FromArgb(143, 242, 194)))
        {
            g.FillPath(badgeBrush, badgePath);
            DrawCentered(g, "LIVE", _smallBoldFont, liveBrush, badgeRect);
        }

        using var separator = new Pen(Color.FromArgb(42, 47, 57), 1f);
        g.DrawLine(separator, OuterPadding, HeaderHeight - 1, ClientSize.Width - OuterPadding, HeaderHeight - 1);
    }

    private void DrawEmptyState(Graphics g)
    {
        var rect = new RectangleF(OuterPadding, HeaderHeight + 12, ClientSize.Width - OuterPadding * 2, 54);
        using var path = RoundedRect(rect, 12);
        using var brush = new SolidBrush(Card);
        using var text = new SolidBrush(TextSecondary);
        g.FillPath(brush, path);
        DrawCentered(g, "Add an account to start monitoring", _bodyFont, text, rect);
    }

    private void DrawAccountCard(Graphics g, OverlayItem item, RectangleF rect)
    {
        _animation.TryGetValue(item.Profile.Id, out var state);
        state ??= new AnimatedAccount();

        using (var path = RoundedRect(rect, 14))
        using (var fill = new SolidBrush(Card))
        using (var border = new Pen(
                   state.Flash > 0.01f
                       ? Blend(CardBorder, Accent, state.Flash * 0.78f)
                       : CardBorder,
                   state.Flash > 0.01f ? 1.4f : 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        var accentColor = AccentFor(item.Profile.Id);
        var avatarRect = new RectangleF(rect.X + 12, rect.Y + 10, 26, 26);
        using (var avatarBrush = new SolidBrush(Color.FromArgb(62, accentColor)))
        using (var avatarText = new SolidBrush(Blend(accentColor, Color.White, 0.28f)))
        {
            g.FillEllipse(avatarBrush, avatarRect);
            var initial = string.IsNullOrWhiteSpace(item.Profile.Label)
                ? "C"
                : item.Profile.Label.Trim()[0].ToString().ToUpperInvariant();
            DrawCentered(g, initial, _smallBoldFont, avatarText, avatarRect);
        }

        var plan = (item.Snapshot.PlanType ?? item.Profile.PlanType ?? "?").ToUpperInvariant();
        var planSize = g.MeasureString(plan, _smallBoldFont);
        var planRect = new RectangleF(
            rect.Right - planSize.Width - 25,
            rect.Y + 10,
            planSize.Width + 14,
            22);

        using (var planPath = RoundedRect(planRect, 11))
        using (var planBrush = new SolidBrush(Color.FromArgb(44, 50, 62)))
        using (var planText = new SolidBrush(Color.FromArgb(194, 201, 213)))
        {
            g.FillPath(planBrush, planPath);
            DrawCentered(g, plan, _smallBoldFont, planText, planRect);
        }

        var nameRect = new RectangleF(rect.X + 46, rect.Y + 7, Math.Max(40, planRect.Left - (rect.X + 54)), 19);
        using (var nameBrush = new SolidBrush(TextPrimary))
        using (var format = EllipsisFormat())
            g.DrawString(item.Profile.Label, _accountFont, nameBrush, nameRect, format);

        var email = item.Snapshot.Email ?? item.Profile.Email ?? "Waiting for account…";
        var emailRect = new RectangleF(rect.X + 46, rect.Y + 25, Math.Max(40, planRect.Left - (rect.X + 54)), 15);
        using (var emailBrush = new SolidBrush(TextSecondary))
        using (var format = EllipsisFormat())
            g.DrawString(email, _smallFont, emailBrush, emailRect, format);

        if (!item.Snapshot.IsSuccess)
        {
            using var errorBrush = new SolidBrush(Critical);
            var errorRect = new RectangleF(rect.X + 12, rect.Y + 57, rect.Width - 24, 38);
            using var format = EllipsisFormat();
            g.DrawString(item.Snapshot.Error ?? "Usage unavailable", _bodyFont, errorBrush, errorRect, format);
            return;
        }

        DrawLimitRow(g, rect.X + 12, rect.Y + 48, rect.Width - 24, item.Snapshot.Primary, state.Primary);
        DrawLimitRow(g, rect.X + 12, rect.Y + 78, rect.Width - 24, item.Snapshot.Secondary, state.Secondary);
    }

    private void DrawLimitRow(
        Graphics g,
        float x,
        float y,
        float width,
        RateLimitWindowSnapshot? window,
        float animatedRemaining)
    {
        if (window is null)
        {
            using var muted = new SolidBrush(TextSecondary);
            g.DrawString("Limit unavailable", _smallFont, muted, x, y);
            return;
        }

        var remaining = Math.Clamp(animatedRemaining, 0f, 100f);
        var color = UsageColor(remaining);
        var leftText = $"{WindowName(window)} · {ResetText(window)}";
        var rightText = $"{Math.Round(remaining):0}%";

        using (var leftBrush = new SolidBrush(TextSecondary))
            g.DrawString(leftText, _smallFont, leftBrush, x, y);

        var valueSize = g.MeasureString(rightText, _smallBoldFont);
        using (var valueBrush = new SolidBrush(TextPrimary))
            g.DrawString(rightText, _smallBoldFont, valueBrush, x + width - valueSize.Width, y - 1);

        var trackRect = new RectangleF(x, y + 17, width, 6);
        using (var trackPath = RoundedRect(trackRect, 3))
        using (var trackBrush = new SolidBrush(Track))
            g.FillPath(trackBrush, trackPath);

        var fillWidth = width * remaining / 100f;
        if (fillWidth < 1f)
            return;

        var fillRect = new RectangleF(x, y + 17, Math.Max(6f, fillWidth), 6);
        using var fillPath = RoundedRect(fillRect, 3);
        using var fillBrush = new SolidBrush(color);
        g.FillPath(fillBrush, fillPath);
    }

    private static string ResetText(RateLimitWindowSnapshot window)
    {
        if (window.ResetsAt is not DateTimeOffset reset)
            return "reset —";

        var local = reset.LocalDateTime;
        return local.Date == DateTime.Today
            ? $"reset {local:HH:mm}"
            : $"reset {local:dd/MM HH:mm}";
    }

    private void ResizeForContent()
    {
        var contentHeight = _items.Count == 0
            ? 66
            : _items.Count * CardHeight + Math.Max(0, _items.Count - 1) * CardGap + 8;

        ClientSize = new Size(OverlayWidth, HeaderHeight + contentHeight + OuterPadding);
        UpdateRoundedRegion();
    }

    private void AnimateFrame()
    {
        var active = false;

        if (Opacity < 0.965)
        {
            Opacity = Math.Min(0.965, Opacity + 0.085);
            active = true;
        }

        foreach (var state in _animation.Values)
        {
            active |= Ease(ref state.Primary, state.PrimaryTarget);
            active |= Ease(ref state.Secondary, state.SecondaryTarget);

            if (state.Flash > 0.01f)
            {
                state.Flash *= 0.88f;
                active = true;
            }
            else
            {
                state.Flash = 0f;
            }
        }

        if (_globalFlash > 0.01f)
        {
            _globalFlash *= 0.86f;
            active = true;
        }
        else
        {
            _globalFlash = 0f;
        }

        Invalidate();

        if (!active)
            _animationTimer.Stop();
    }

    private static bool Ease(ref float current, float target)
    {
        var delta = target - current;
        if (Math.Abs(delta) < 0.08f)
        {
            current = target;
            return false;
        }

        current += delta * 0.18f;
        return true;
    }

    private static Color UsageColor(float remaining)
    {
        if (remaining >= 50f)
            return Healthy;
        if (remaining >= 20f)
            return Warning;
        return Critical;
    }

    private static Color AccentFor(string id)
    {
        var palette = new[]
        {
            Color.FromArgb(111, 126, 255),
            Color.FromArgb(113, 204, 255),
            Color.FromArgb(179, 126, 255),
            Color.FromArgb(255, 137, 190),
            Color.FromArgb(93, 220, 178)
        };

        var hash = 17;
        foreach (var c in id)
            hash = unchecked(hash * 31 + c);

        var index = (int)((uint)hash % (uint)palette.Length);
        return palette[index];
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();

        if (diameter <= 1f)
        {
            path.AddRectangle(rect);
            path.CloseFigure();
            return path;
        }

        var arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static StringFormat EllipsisFormat() => new()
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, rect, format);
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        using var path = RoundedRect(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), CornerRadius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        _dragging = true;
        _dragOriginScreen = MousePosition;
        _dragOriginForm = Location;
        Capture = true;
    }

    private void ContinueDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var current = MousePosition;
        Location = new Point(
            _dragOriginForm.X + current.X - _dragOriginScreen.X,
            _dragOriginForm.Y + current.Y - _dragOriginScreen.Y);
    }

    private void EndDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Capture = false;
        OverlayMoved?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _menu.Dispose();
            _titleFont.Dispose();
            _accountFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
            _smallBoldFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record OverlayItem(CodexAccountProfile Profile, UsageSnapshot Snapshot);

    private sealed class AnimatedAccount
    {
        public float Primary;
        public float Secondary;
        public float PrimaryTarget;
        public float SecondaryTarget;
        public float Flash;
    }
}
