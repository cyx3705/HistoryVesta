using System.Reflection;
using BaseVariable;
using ProjectPulse;
using ToolKit;
using ToolRelay;

var assembly = typeof(Kit).Assembly;
var moduleInfos = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
    .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
    .OrderBy(info => info.ModuleName, StringComparer.OrdinalIgnoreCase)
    .ToList();

Equal(3, moduleInfos.Count, "聚合程序集必须包含三个逻辑模块入口");
SequenceEqual(
    new[] { "ProjectPulse", "ToolKit", "ToolRelay" },
    moduleInfos.Select(info => info.ModuleName).ToArray(),
    "逻辑模块名必须保持兼容");
True(moduleInfos.All(info => info.Version == "1.2.0"), "逻辑模块版本必须统一为 1.2.0");

var commandCounts = moduleInfos.ToDictionary(
    info => info.ModuleName,
    info => info.MainClassType!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Count(method => !method.IsSpecialName),
    StringComparer.OrdinalIgnoreCase);
Equal(3, commandCounts["ProjectPulse"], "ProjectPulse 指令数");
Equal(4, commandCounts["ToolKit"], "ToolKit 指令数");
Equal(3, commandCounts["ToolRelay"], "ToolRelay 指令数");

// 活动坞已迁出为独立模块 ActiveDock，聚合程序集不再承载任何界面生命周期。
Equal(
    0,
    assembly.GetTypes().Count(type => type.IsPublic && !type.IsAbstract
        && typeof(AppShell.Core.Modules.IUiModule).IsAssignableFrom(type)),
    "聚合程序集不得再注册 UI 生命周期");

var kit = new Kit();
Equal(
    "2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824",
    kit.Sha256("hello"),
    "ToolKit SHA-256 回归");
Equal("aGVsbG8=", kit.Base64Encode("hello"), "ToolKit Base64 回归");
True(Guid.TryParse(kit.NewGuid(), out _), "ToolKit GUID 回归");

True(typeof(ProjectPulseCommands).Assembly == assembly, "ProjectPulse 必须进入聚合程序集");
True(typeof(RelayCommands).Assembly == assembly, "ToolRelay 必须进入聚合程序集");
Console.WriteLine("StudioTools.Smoke: PASS (3 modules, 10 commands, 0 UI modules)");

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
}

static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{message}: expected={string.Join(',', expected)}, actual={string.Join(',', actual)}");
}
