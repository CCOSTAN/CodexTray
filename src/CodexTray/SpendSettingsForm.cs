namespace CodexTray;

internal sealed class SpendSettingsForm : Form
{
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 355, AccessibleName = "Organization admin key" };
    public string AdminKey => _key.Text.Trim();

    public SpendSettingsForm(bool hasKey)
    {
        Text = "API spend settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var panel = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(15)
        };
        var instructions = new Label
        {
            AutoSize = true, MaximumSize = new Size(355, 0),
            Text = "Connect an OpenAI organization admin key to read monthly costs. Windows encrypts the key for your user account."
        };
        var link = new LinkLabel { AutoSize = true, Text = "Open OpenAI admin keys", Margin = new Padding(3, 6, 3, 12) };
        link.LinkClicked += (_, _) => Brand.OpenUrl(Brand.AdminKeysUrl);
        var label = new Label { AutoSize = true, Text = hasKey ? "Replacement organization admin key" : "Organization admin key" };
        var save = new Button { Text = "Save", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([save, cancel]);
        if (hasKey)
        {
            buttons.Controls.Add(new Button { Text = "Disconnect", AutoSize = true, DialogResult = DialogResult.No });
        }
        _key.TextChanged += (_, _) => save.Enabled = !string.IsNullOrWhiteSpace(_key.Text);
        panel.Controls.AddRange([instructions, link, label, _key, buttons]);
        Controls.Add(panel);
        AcceptButton = save;
        CancelButton = cancel;
        ActiveControl = _key;
    }
}
