namespace CodexUsageHub.UI;

internal sealed class PromptDialog : Form
{
    private readonly TextBox _textBox = new();

    public string Value => _textBox.Text;

    public PromptDialog(string title, string prompt, string defaultValue = "")
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 140);

        var label = new Label
        {
            Text = prompt,
            AutoSize = true,
            Location = new Point(14, 16)
        };

        _textBox.Location = new Point(14, 44);
        _textBox.Width = 392;
        _textBox.Text = defaultValue;

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(250, 90),
            Width = 75
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(331, 90),
            Width = 75
        };

        Controls.AddRange([label, _textBox, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
