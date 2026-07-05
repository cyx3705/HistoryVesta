namespace b_Code_三维坐标机器人前端设计;

/// <summary>
/// CMD 风格合并控制台：历史输出与当前输入行在同一文本框内。
/// </summary>
public sealed class ConsoleControl : UserControl
{
    private readonly RichTextBox _console;
    private int _inputStart;
    private const string Prompt = "> ";

    public event Func<string, string>? CommandExecuted;

    public ConsoleControl()
    {
        _console = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(12, 16, 22),
            ForeColor = Color.FromArgb(210, 220, 230),
            Font = new Font("Consolas", 10F),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
            DetectUrls = false
        };
        _console.KeyDown += Console_KeyDown;
        _console.MouseDown += Console_MouseDown;
        Controls.Add(_console);
        WriteSystemLine("三维坐标机器人路径规划控制台");
        WriteSystemLine("输入 help 查看指令，Enter 执行当前行。");
        NewPrompt();
    }

    public void WriteSystemLine(string text) =>
        AppendHistory($"{text}{Environment.NewLine}");

    public void WriteOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (string line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
            AppendHistory($"{line}{Environment.NewLine}");
    }

    public void RunScript(string script)
    {
        foreach (string raw in script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            ExecuteLine(line);
        }
    }

    public void AppendExecutedCommand(string command, string result)
    {
        AppendHistory($"{Prompt}{command}{Environment.NewLine}");
        WriteOutput(result);
        NewPrompt();
    }

    private void NewPrompt()
    {
        AppendHistory(Prompt);
        _inputStart = _console.TextLength;
        _console.SelectionStart = _console.TextLength;
        _console.ScrollToCaret();
    }

    private void AppendHistory(string text) => _console.AppendText(text);

    private void Console_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_console.SelectionStart < _inputStart)
        {
            _console.SelectionStart = _console.TextLength;
            _console.ScrollToCaret();
        }
    }

    private void Console_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
        {
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.C)
            return;

        if (_console.SelectionStart < _inputStart && e.KeyCode is not (Keys.Left or Keys.Right or Keys.Home or Keys.End))
        {
            _console.SelectionStart = _console.TextLength;
        }

        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string command = _console.Text[_inputStart..].Trim();
            ExecuteLine(command);
        }
    }

    private void ExecuteLine(string command)
    {
        AppendHistory(Environment.NewLine);
        if (command.Length > 0)
        {
            string result = CommandExecuted?.Invoke(command) ?? string.Empty;
            WriteOutput(result);
        }

        NewPrompt();
    }
}