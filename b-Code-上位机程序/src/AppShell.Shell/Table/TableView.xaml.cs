using System.Data;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AppShell.Core.Commands;
using AppShell.Core.Data;
using AppShell.Core.Logging;

namespace AppShell.Shell.Table;

/// <summary>
/// 表窗口(§4.3):程序所有数据库的统一查看与修改入口,
/// 本质是 db.* 指令组的图形外壳——查询 / 编辑 / 删除全部
/// 先生成等价指令文本、经指令总线执行、控制台回显(验收 4)。
/// 行定位依赖查询结果首列 __rowid__;无该列的结果只读。
/// </summary>
public partial class TableView : UserControl
{
    private const int PageSize = 500; // T-03 默认每页行数

    private readonly IDataService _data;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly DispatcherTimer _reloadDebounce;

    // 当前查询状态
    private string _conn = IDataService.DefaultConnection;
    private string? _table;
    private string? _where;
    private int _page = 1;
    private QueryResult? _current;
    private bool _hasRowId;

    private bool _loading;   // 填充网格期间抑制编辑事件
    private bool _syncing;   // 程序驱动下拉选择期间抑制查询

    public TableView(IDataService data, CommandBus bus, IShellLog log)
    {
        InitializeComponent();

        _data = data;
        _bus = bus;
        _log = log;

        // 数据变更(任意路径:手输指令 / 脚本 / 面板)→ 正在显示该表时自动重载(验收 4)
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            ReloadSilent();
        };
        _data.DataChanged += (conn, table) => Dispatcher.BeginInvoke(() =>
        {
            if (_table != null
                && conn.Equals(_conn, StringComparison.OrdinalIgnoreCase)
                && (table == null || table.Equals(_table, StringComparison.OrdinalIgnoreCase)))
            {
                _reloadDebounce.Stop();
                _reloadDebounce.Start();
            }
        });

        Loaded += (_, _) =>
        {
            if (ConnCombo.Items.Count == 0)
                InitialLoad();
        };
    }

    // ---------------------------------------------------------------- 对外入口(db.* 指令调用)

    /// <summary>db.query 执行后同步显示结果(附录 C:“表窗口已同步显示”)。UI 线程调用。</summary>
    public void ShowResult(string conn, string table, string? where, QueryResult result)
    {
        _conn = conn;
        _table = table;
        _where = where;
        _page = result.Page;
        SyncSelectors();
        Populate(result);
    }

    /// <summary>db.sql 的 SELECT 结果:只读显示,无分页上下文。UI 线程调用。</summary>
    public void ShowAdhoc(QueryResult result)
    {
        _table = null;
        _where = null;
        _page = 1;
        Populate(result);
        HintText.Text = "SQL 直通结果,只读";
    }

    // ---------------------------------------------------------------- 加载与填充

    private void InitialLoad()
    {
        try
        {
            _syncing = true;
            ConnCombo.ItemsSource = _data.ListConnections();
            ConnCombo.SelectedItem = ConnCombo.Items.Cast<string>()
                .FirstOrDefault(c => c.Equals(_conn, StringComparison.OrdinalIgnoreCase))
                ?? ConnCombo.Items.Cast<string>().FirstOrDefault();
            _conn = ConnCombo.SelectedItem as string ?? _conn;

            var tables = _data.ListTables(_conn);
            TableCombo.ItemsSource = tables;
            TableCombo.SelectedItem = tables.FirstOrDefault();
            _table = TableCombo.SelectedItem as string;
            _syncing = false;

            RebuildFilterColumns();
            ReloadSilent();
        }
        catch (Exception ex)
        {
            _syncing = false;
            _log.Error("table", $"表窗口初始化失败: {ex.Message}");
            HintText.Text = ex.Message;
        }
    }

    /// <summary>静默重载当前页(初始化与数据变更自动刷新;用户主动操作走指令)。</summary>
    private void ReloadSilent()
    {
        if (_table == null)
            return;

        try
        {
            var result = _data.Query(_table, _where, null, PageSize, _page, _conn);

            // 删除导致当前页超界时回退到末页
            if (result.Rows.Count == 0 && result.TotalRows > 0 && _page > result.TotalPages)
            {
                _page = result.TotalPages;
                result = _data.Query(_table, _where, null, PageSize, _page, _conn);
            }

            Populate(result);
        }
        catch (Exception ex)
        {
            _log.Error("table", $"查询失败: {ex.Message}");
            HintText.Text = ex.Message;
        }
    }

    private void Populate(QueryResult result)
    {
        _loading = true;
        try
        {
            _current = result;
            _hasRowId = result.Columns.Count > 0 && result.Columns[0] == "__rowid__";

            var table = new DataTable();
            foreach (var col in result.Columns)
            {
                var type = result.Rows
                    .Select(r => r[table.Columns.Count])
                    .FirstOrDefault(v => v != null)?.GetType() ?? typeof(object);
                table.Columns.Add(col, type);
            }

            foreach (var row in result.Rows)
                table.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());

            Grid.ItemsSource = table.DefaultView;
            Grid.IsReadOnly = !_hasRowId;

            PageText.Text = $"第 {result.Page} / {result.TotalPages} 页";
            TotalText.Text = $"共 {result.TotalRows} 行";
            HintText.Text = _hasRowId
                ? "双击单元格编辑,末行新增;修改即时生成 db.* 指令"
                : "结果无行定位列,只读";
            PageFirst.IsEnabled = PagePrev.IsEnabled = result.Page > 1;
            PageNext.IsEnabled = PageLast.IsEnabled = result.Page < result.TotalPages;

            if (SchemaToggle.IsChecked == true && _table != null)
                LoadSchema();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SyncSelectors()
    {
        _syncing = true;
        try
        {
            if (ConnCombo.SelectedItem as string != _conn)
                ConnCombo.SelectedItem = ConnCombo.Items.Cast<string>()
                    .FirstOrDefault(c => c.Equals(_conn, StringComparison.OrdinalIgnoreCase));

            var tables = _data.ListTables(_conn);
            TableCombo.ItemsSource = tables;
            TableCombo.SelectedItem = tables.FirstOrDefault(
                t => t.Equals(_table, StringComparison.OrdinalIgnoreCase));
            RebuildFilterColumns();
        }
        catch (Exception ex)
        {
            _log.Error("table", $"同步选择器失败: {ex.Message}");
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------------------------------------------------------- 用户动作 → 指令

    /// <summary>用户主动查询动作统一出口:拼 db.query 指令经总线(回显 [UI],验收 4)。</summary>
    private void SendQuery()
    {
        if (_table == null)
            return;

        var sb = new StringBuilder($"db.query table={CommandParser.QuoteArg(_table)}");
        if (!string.IsNullOrWhiteSpace(_where))
            sb.Append($" where={CommandParser.QuoteArg(_where)}");
        if (_page > 1)
            sb.Append($" page={_page}");
        AppendConn(sb);
        _ = _bus.ExecuteAsync(sb.ToString(), "UI");
    }

    private void AppendConn(StringBuilder sb)
    {
        if (!_conn.Equals(IDataService.DefaultConnection, StringComparison.OrdinalIgnoreCase))
            sb.Append($" conn={CommandParser.QuoteArg(_conn)}");
    }

    private void OnConnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _loading || ConnCombo.SelectedItem is not string conn)
            return;

        _conn = conn;
        _syncing = true;
        var tables = _data.ListTables(_conn);
        TableCombo.ItemsSource = tables;
        TableCombo.SelectedItem = tables.FirstOrDefault();
        _table = TableCombo.SelectedItem as string;
        _syncing = false;

        _page = 1;
        _where = null;
        FilterHost.Children.Clear();
        RebuildFilterColumns();
        SendQuery();
    }

    private void OnTableChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _loading || TableCombo.SelectedItem is not string table)
            return;

        _table = table;
        _page = 1;
        _where = null;
        FilterHost.Children.Clear();
        RebuildFilterColumns();
        SendQuery();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => SendQuery();

    private void OnPageClick(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;

        _page = (sender as Button)?.Tag switch
        {
            "first" => 1,
            "prev" => Math.Max(1, _page - 1),
            "next" => Math.Min(_current.TotalPages, _page + 1),
            _ => _current.TotalPages,
        };
        SendQuery();
    }

    // ---------------------------------------------------------------- 筛选(T-04)

    private static readonly string[] Operators = ["=", "!=", ">", ">=", "<", "<=", "LIKE"];

    private void OnAddFilterClick(object sender, RoutedEventArgs e) => AddFilterRow();

    private void AddFilterRow()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 2) };
        var col = new ComboBox { Width = 100, ItemsSource = CurrentColumns(), SelectedIndex = 0 };
        var op = new ComboBox { Width = 60, ItemsSource = Operators, SelectedIndex = 0, Margin = new Thickness(4, 0, 0, 0) };
        var value = new TextBox { Width = 110, Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = "×", Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        remove.Click += (_, _) => FilterHost.Children.Remove(panel);
        panel.Children.Add(col);
        panel.Children.Add(op);
        panel.Children.Add(value);
        panel.Children.Add(remove);
        FilterHost.Children.Add(panel);
    }

    private List<string> CurrentColumns()
        => _current?.Columns.Where(c => c != "__rowid__").ToList() ?? [];

    private void RebuildFilterColumns()
    {
        foreach (var child in FilterHost.Children.OfType<StackPanel>())
        {
            if (child.Children[0] is ComboBox col)
                col.ItemsSource = CurrentColumns();
        }
    }

    /// <summary>把筛选行拼成 where 片段(多条 AND,T-04);无有效条件返回 null。</summary>
    private string? BuildWhere()
    {
        var parts = new List<string>();
        foreach (var row in FilterHost.Children.OfType<StackPanel>())
        {
            if (row.Children[0] is not ComboBox col || col.SelectedItem is not string column)
                continue;
            var op = (row.Children[1] as ComboBox)?.SelectedItem as string ?? "=";
            var raw = (row.Children[2] as TextBox)?.Text ?? "";
            if (raw.Length == 0)
                continue;

            parts.Add(op == "LIKE"
                ? $"{column} LIKE {SqlLiteral($"%{raw}%")}"
                : $"{column} {op} {SqlLiteral(raw)}");
        }

        return parts.Count > 0 ? string.Join(" AND ", parts) : null;
    }

    private void OnApplyFilterClick(object sender, RoutedEventArgs e)
    {
        _where = BuildWhere();
        _page = 1;
        SendQuery();
    }

    /// <summary>
    /// 按当前筛选删除(T-08 的 UI 路径):筛选为空时生成不带 where 的
    /// db.delete —— 由总线的二次确认拦截器拦下(验收 9)。
    /// </summary>
    private void OnDeleteByFilterClick(object sender, RoutedEventArgs e)
    {
        if (_table == null)
            return;

        var where = BuildWhere();
        var sb = new StringBuilder($"db.delete table={CommandParser.QuoteArg(_table)}");
        if (where != null)
            sb.Append($" where={CommandParser.QuoteArg(where)}");
        AppendConn(sb);
        _ = _bus.ExecuteAsync(sb.ToString(), "UI");
    }

    // ---------------------------------------------------------------- 行编辑(T-05:即时逐条提交)

    private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "__rowid__")
            e.Cancel = true;
    }

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_loading || _table == null || !_hasRowId
            || e.EditAction != DataGridEditAction.Commit
            || e.Row.IsNewItem)
        {
            return;
        }

        var column = e.Column.Header as string;
        if (column is null or "__rowid__" || e.Row.Item is not DataRowView drv)
            return;

        var newText = e.EditingElement switch
        {
            TextBox tb => tb.Text,
            CheckBox cb => cb.IsChecked == true ? "1" : "0",
            _ => null,
        };
        if (newText == null)
            return;

        var oldValue = drv.Row[column];
        var oldText = oldValue == DBNull.Value ? "" : Convert.ToString(oldValue, CultureInfo.InvariantCulture) ?? "";
        if (newText == oldText)
            return;

        var rowid = Convert.ToString(drv.Row["__rowid__"], CultureInfo.InvariantCulture);
        var command = new StringBuilder(
            $"db.update table={CommandParser.QuoteArg(_table)} " +
            $"set={CommandParser.QuoteArg($"{column}={SqlLiteral(newText)}")} " +
            $"where={CommandParser.QuoteArg($"rowid={rowid}")}");
        AppendConn(command);
        _ = _bus.ExecuteAsync(command.ToString(), "UI");
    }

    private void OnRowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (_loading || _table == null || !_hasRowId
            || e.EditAction != DataGridEditAction.Commit
            || !e.Row.IsNewItem
            || e.Row.Item is not DataRowView drv)
        {
            return;
        }

        var assignments = new List<string>();
        foreach (var col in CurrentColumns())
        {
            var value = drv[col];
            if (value == null || value == DBNull.Value)
                continue;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (text.Length == 0)
                continue;
            assignments.Add($"{col}={SqlLiteral(text)}");
        }

        if (assignments.Count == 0)
            return;

        var command = new StringBuilder(
            $"db.insert table={CommandParser.QuoteArg(_table)} " +
            $"set={CommandParser.QuoteArg(string.Join(", ", assignments))}");
        AppendConn(command);
        _ = _bus.ExecuteAsync(command.ToString(), "UI");
    }

    /// <summary>删除选中行(带 rowid 的精确 where;先在 UI 侧确认,T-08)。</summary>
    private void OnDeleteRowsClick(object sender, RoutedEventArgs e)
    {
        if (_table == null || !_hasRowId)
            return;

        var rowids = Grid.SelectedCells
            .Select(c => c.Item)
            .OfType<DataRowView>()
            .Where(r => !r.IsNew)
            .Select(r => Convert.ToString(r.Row["__rowid__"], CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        if (rowids.Count == 0)
        {
            HintText.Text = "请先选中要删除的行";
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!,
                $"删除选中的 {rowids.Count} 行?",
                "需要确认", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var where = rowids.Count == 1
            ? $"rowid={rowids[0]}"
            : $"rowid IN ({string.Join(",", rowids)})";
        var command = new StringBuilder(
            $"db.delete table={CommandParser.QuoteArg(_table)} where={CommandParser.QuoteArg(where)}");
        AppendConn(command);
        _ = _bus.ExecuteAsync(command.ToString(), "UI");
    }

    // ---------------------------------------------------------------- 结构视图(T-07)/ 导出(T-09)

    private void OnSchemaToggled(object sender, RoutedEventArgs e)
    {
        var showSchema = SchemaToggle.IsChecked == true;
        SchemaGrid.Visibility = showSchema ? Visibility.Visible : Visibility.Collapsed;
        Grid.Visibility = showSchema ? Visibility.Collapsed : Visibility.Visible;
        if (showSchema)
            LoadSchema();
    }

    private void LoadSchema()
    {
        if (_table == null)
            return;

        try
        {
            SchemaGrid.ItemsSource = _data.GetSchema(_table, _conn)
                .Select(c => new
                {
                    字段名 = c.Name,
                    类型 = c.Type,
                    主键 = c.IsPrimaryKey ? "是" : "",
                    非空 = c.NotNull ? "是" : "",
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Error("table", $"读取表结构失败: {ex.Message}");
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_table == null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出当前查询结果为 CSV",
            FileName = $"{_table}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder(
            $"db.export table={CommandParser.QuoteArg(_table)} file={CommandParser.QuoteArg(dialog.FileName)}");
        if (!string.IsNullOrWhiteSpace(_where))
            sb.Append($" where={CommandParser.QuoteArg(_where)}");
        AppendConn(sb);
        _ = _bus.ExecuteAsync(sb.ToString(), "UI");
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>把用户输入文本编码为 SQL 字面量:纯数值裸写,其余单引号包裹('' 转义)。</summary>
    private static string SqlLiteral(string text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
            ? text
            : $"'{text.Replace("'", "''")}'";
}
