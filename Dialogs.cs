namespace miniCar;

/// <summary>트레이 앱에서 띄우는 작은 대화 상자 (다른 창들 위에 뜨도록)</summary>
internal static class Dialogs
{
    /// <summary>맨 위에 뜨는 메시지 상자</summary>
    public static DialogResult Ask(string text, MessageBoxButtons buttons, MessageBoxIcon icon = MessageBoxIcon.Question,
        MessageBoxDefaultButton def = MessageBoxDefaultButton.Button1)
    {
        using var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
        };
        owner.Show();
        owner.Activate();
        return MessageBox.Show(owner, text, "miniCar", buttons, icon, def);
    }

    /// <summary>트랙 이름 입력. 취소하면 null</summary>
    public static string? AskTrackName(string title, string initial, IReadOnlyList<string> existing)
    {
        using var form = new Form
        {
            Text = "miniCar · " + title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            ShowInTaskbar = true,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96),
            ClientSize = new Size(380, 150),
            Font = new Font("Malgun Gothic", 9f),
        };
        var label = new Label { Text = "트랙 이름 (목록에서 고르면 그 트랙에 덮어씀):", Left = 14, Top = 14, Width = 352 };
        var box = new ComboBox { Left = 14, Top = 40, Width = 352, DropDownStyle = ComboBoxStyle.DropDown, Text = initial };
        box.Items.AddRange(existing.Cast<object>().ToArray());
        box.Text = initial;
        var error = new Label { Left = 14, Top = 70, Width = 352, Height = 34, ForeColor = Color.Firebrick };
        var ok = new Button { Text = "저장", Left = 200, Top = 108, Width = 80, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "취소", Left = 286, Top = 108, Width = 80, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, box, error, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        string? result = null;
        ok.Click += (_, _) =>
        {
            string name = box.Text.Trim();
            string? problem = TrackStore.Validate(name);
            if (problem != null) { error.Text = problem; return; }
            result = name;
            form.DialogResult = DialogResult.OK;
        };
        form.Shown += (_, _) => { form.Activate(); box.Focus(); box.SelectAll(); };
        return form.ShowDialog() == DialogResult.OK ? result : null;
    }
}
