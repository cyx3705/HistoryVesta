using System.IO;
using System.Reflection;
using ActiveDock;
using AppShell.Core.Modules;
using BaseVariable;

var assembly = typeof(ActiveDockCommands).Assembly;

var moduleInfos = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
    .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
    .ToList();

Equal(1, moduleInfos.Count, "独立程序集必须只有一个模块入口");
Equal("dock", moduleInfos[0].ModuleName, "命令域必须沿用 dock");
Equal("1.1.1", moduleInfos[0].Version, "模块版本");
Equal(typeof(ActiveDockCommands), moduleInfos[0].MainClassType, "命令入口类型");

Equal(
    6,
    moduleInfos[0].MainClassType!
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Count(method => !method.IsSpecialName),
    "dock 指令数必须与聚合期保持一致");

var uiTypes = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(IUiModule).IsAssignableFrom(type))
    .ToList();
Equal(1, uiTypes.Count, "独立程序集必须只注册一个 UI 生命周期");
Equal(typeof(ActiveDockUiModule), uiTypes[0], "UI 生命周期类型");

// 宿主探针：注入过 ShellUi 即视为桌面 Shell 进程，此时必须弃权不建窗。
True(
    typeof(IShellUiAware).IsAssignableFrom(typeof(ActiveDockUiModule)),
    "必须实现 IShellUiAware 才能被宿主注入并据此判定归属");
var shellHosted = new ActiveDockUiModule();
((IShellUiAware)shellHosted).ShellUi = new NullRegistrar();
shellHosted.CreateUi();
shellHosted.DestroyUi();

var cache = Path.Combine(Path.GetTempPath(), "activedock-icons-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cache);
try
{
    var bitmap = ProjectIconGenerator.Create("2026-020-中文项目", cache);
    Equal(64, bitmap.PixelWidth, "活动坞图标宽度");
    Equal(64, bitmap.PixelHeight, "活动坞图标高度");
}
finally
{
    Directory.Delete(cache, recursive: true);
}

// V1.1.1 布局规则：右下角锚定、尺寸钳制、只开左/上/左上的调整命中区。
var work = new System.Windows.Rect(0, 0, 1920, 1040);
var (anchorLeft, anchorTop) = DockLayout.Anchor(work, 360, 200);
Equal(1920 - 360 - DockLayout.Margin, anchorLeft, "锚点左边界");
Equal(1040 - 200 - DockLayout.Margin, anchorTop, "锚点上边界");

// 尺寸变大后右下角必须不动：右下角 = Left+Width、Top+Height 应保持不变。
var (wideLeft, tallTop) = DockLayout.Anchor(work, 500, 300);
Equal(anchorLeft + 360, wideLeft + 500, "加宽后右边界不动");
Equal(anchorTop + 200, tallTop + 300, "加高后下边界不动");

Equal(DockLayout.MinWidth, DockLayout.ClampWidth(10), "宽度下限");
Equal(DockLayout.MaxWidth, DockLayout.ClampWidth(9999), "宽度上限");
Equal(DockLayout.MinHeight, DockLayout.ClampHeight(1), "高度下限");
Equal(DockLayout.MaxHeight, DockLayout.ClampHeight(9999), "高度上限");
Equal(DockLayout.DefaultWidth, DockLayout.ClampWidth(double.NaN), "宽度 NaN 回退缺省");
Equal(DockLayout.DefaultHeight, DockLayout.ClampHeight(0), "高度非正回退缺省");

Equal(DockLayout.HitTopLeft, DockLayout.HitTest(2, 2, 360, 200), "左上角可调");
Equal(DockLayout.HitLeft, DockLayout.HitTest(2, 100, 360, 200), "左边框可调");
Equal(DockLayout.HitTop, DockLayout.HitTest(100, 2, 360, 200), "上边框可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(358, 198, 360, 200), "右下角不可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(358, 100, 360, 200), "右边框不可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(100, 198, 360, 200), "下边框不可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(100, 100, 360, 200), "窗口体不可调");

Console.WriteLine(
    "ActiveDock.Smoke: PASS (1 module, 6 commands, 1 UI module, shell-hosted abstain, bottom-right layout)");

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

/// <summary>只用于探针断言；被调用即说明弃权逻辑失效。</summary>
file sealed class NullRegistrar : IShellUiRegistrar
{
    public bool IsUiThread => throw Fail();

    public void Invoke(Action action) => throw Fail();

    public IDisposable RegisterToolWindow(AppShell.Core.Docking.ToolWindowDescriptor descriptor, string owner)
        => throw Fail();

    public void UnregisterToolWindow(string id) => throw Fail();

    public void UnregisterOwner(string owner) => throw Fail();

    private static InvalidOperationException Fail()
        => new("桌面 Shell 进程中活动坞必须弃权，不得触碰宿主界面注册器");
}
