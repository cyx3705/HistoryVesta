using AppShell.Core;
using OneHistoryStudio.Smoke;
using OneHistoryStudio.Smoke.Suites;

// 用法：
//   Smoke.exe                                      依次运行全部默认套件
//   Smoke.exe --suite RepositoryTargets            只运行指定功能套件
//   Smoke.exe --suite GitRules --real-template     向功能套件透传可选参数
//   Smoke.exe --suite Docking --real-mouse         在隔离窗口执行真实鼠标交互

// 冒烟宿主不运行 WPF 装配点，因此在此显式登记应用身份。
AppIdentity.Use(typeof(OneHistoryStudio.Git.ProjectService).Assembly);
Environment.CurrentDirectory = SmokeKit.RepoRoot;

var suites = new (string Name, Func<string[], Task> Run)[]
{
    ("Wiring", WiringSuite.RunAsync),
    ("VersionProjection", VersionProjectionSuite.RunAsync),
    ("TestArchitecture", TestArchitectureSuite.RunAsync),
    ("GitRules", GitRulesSuite.RunAsync),
    ("PromptGovernance", PromptGovernanceSuite.RunAsync),
    ("BranchHistory", BranchHistorySuite.RunAsync),
    ("SubmoduleSafety", SubmoduleSafetySuite.RunAsync),
    ("RepositoryTargets", RepositoryTargetsSuite.RunAsync),
    ("ServiceWeb", ServiceWebSuite.RunAsync),
    ("Docking", DockingSuite.RunAsync),
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

    try
    {
        await suite.Run(args);
        Console.WriteLine($"{suite.Name}Smoke: PASS");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        Console.Error.WriteLine($"{suite.Name}Smoke: FAIL");
        failed++;
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
