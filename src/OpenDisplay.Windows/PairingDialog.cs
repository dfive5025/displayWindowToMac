namespace OpenDisplay.Windows;

/// <summary>Asks for the 6-digit code that KeyBridge prints on the Mac.</summary>
internal sealed class PairingDialog : Form
{
    private readonly TextBox _code = new() { MaxLength = 6, Width = 160, Font = new Font("Segoe UI", 16) };

    public string Code => _code.Text.Trim();

    public PairingDialog(string? current, string reason)
    {
        Text = "Keyboard to Mac";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        _code.Text = current ?? "";

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Text = reason + "\n\nOn the Mac, run KeyBridge. It prints a 6-digit pairing code. Type that code here.",
        });
        layout.Controls.Add(_code);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Not now", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        _code.KeyPress += (_, e) => e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
    }
}
