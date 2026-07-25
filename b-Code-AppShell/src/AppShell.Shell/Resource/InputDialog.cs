using System.Windows;
using System.Windows.Controls;

namespace AppShell.Shell.Resource;

/// <summary>极简文本输入对话框(新建文件夹 / 重命名取名用)。</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;

    public InputDialog(Window owner, string title, string prompt, string initial = "")
    {
        Owner = owner;
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children = { new TextBlock { Text = prompt }, _box, buttons },
        };

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    public string Value => _box.Text;

    /// <summary>返回输入文本;取消返回 null。</summary>
    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(owner, title, prompt, initial);
        return dialog.ShowDialog() == true ? dialog.Value.Trim() : null;
    }
}
