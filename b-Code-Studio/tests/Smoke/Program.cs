using AppShell.Core;
using OneHistoryStudio.Smoke;
using OneHistoryStudio.Smoke.Suites;

// V2.3.3 QC-02:五套冒烟用例的单一宿主。
// 用法:
//   Smoke.exe                      依次跑全部用例,任一失败则退出码非零
//   Smoke.exe --suite V232         只跑指定用例
//   Smoke.exe --suite V213 --real-template   透传其余参数给用例
//   Smoke.exe --suite Docking --real-mouse   隔离窗口内执行真实鼠标拖出/拖回
//
// 合并前每套用例是独立 Exe,各自 ProjectReference App.csproj,于是把整个应用输出
// 复制 5 份 × 2 配置(270M)。合并后只剩 1 份 × 2 配置。

// 应用身份由装配点显式登记;冒烟宿主不跑 WPF 装配点,故在此完成等价动作。
// V2.4.4:只读指令登记已取消——只读性由各 CommandDescriptor.Readonly 自描述。
AppIdentity.Use(typeof(OneHistoryStudio.Git.ProjectService).Assembly);

var suites = new (string Name, Func<string[], Task> Run)[]
{
    ("Wiring", WiringSuite.RunAsync),
    ("VersionProjection", VersionProjectionSuite.RunAsync),
    ("V213", V213Suite.RunAsync),
    ("PromptGovernance", PromptGovernanceSuite.RunAsync),
    ("V230", V230Suite.RunAsync),
    ("V231", V231Suite.RunAsync),
    ("V232", V232Suite.RunAsync),
    ("ServiceWeb", ServiceWebSuite.RunAsync),
    ("Docking", DockingSuite.RunAsync),
    ("GitHubAccount", GitHubAccountSuite.RunAsync),
    ("LanSingleExe", LanSingleExeSuite.RunAsync),
};

var selected = ReadSuiteName(args);
if (selected != null
    && !suites.Any(suite => suite.Name.Equals(selected, StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine(
        $"未知用例: {selected};可选: {string.Join(", ", suites.Select(suite => suite.Name))}");
    return 2;
}

var failed = 0;
foreach (var suite in suites)
{
    if (selected != null && !suite.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))
        continue;

    // 各用例的历史工作目录语义原样保留:合并前 V213 与 PromptGovernance 在父目录下运行,
    // V230~V232 在仓库根下运行。宿主逐套显式设置,避免合并改变任何一条判据。
    var previous = Environment.CurrentDirectory;
    try
    {
        await suite.Run(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        Console.WriteLine($"{suite.Name}Smoke: FAIL");
        failed++;
    }
    finally
    {
        Environment.CurrentDirectory = previous;
    }
}

return failed == 0 ? 0 : 1;

static string? ReadSuiteName(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--suite", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}
