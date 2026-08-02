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
Equal("2.0.0", moduleInfos[0].Version, "模块版本");
Equal(typeof(ActiveDockCommands), moduleInfos[0].MainClassType, "命令入口类型");

Equal(
    12,
    moduleInfos[0].MainClassType!
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Count(method => !method.IsSpecialName),
    "dock 指令数(2.0.0 新增 open/usage/forget/exclude/include/policy)");

var uiTypes = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(IUiModule).IsAssignableFrom(type))
    .ToList();
Equal(1, uiTypes.Count, "独立程序集必须只注册一个 UI 生命周期");
Equal(typeof(ActiveDockUiModule), uiTypes[0], "UI 生命周期类型");

// 宿主判据：注入过 ShellUi 即桌面 Shell 进程，2.0.0 起该侧注册扩展坞管理页面而不是活动坞窗口。
True(
    typeof(IShellUiAware).IsAssignableFrom(typeof(ActiveDockUiModule)),
    "必须实现 IShellUiAware 才能被宿主注入并据此判定归属");
var registrar = new RecordingRegistrar();
var shellHosted = new ActiveDockUiModule();
((IShellUiAware)shellHosted).ShellUi = registrar;
shellHosted.CreateUi();
Equal(1, registrar.Registered.Count, "桌面侧必须注册且只注册一次管理页面");
Equal("dock.manager", registrar.Registered[0], "桌面侧注册的必须是管理页面");
shellHosted.CreateUi();
Equal(1, registrar.Registered.Count, "重复 CreateUi 不得重复注册");
shellHosted.DestroyUi();
Equal(1, registrar.Disposed, "DestroyUi 必须释放管理页面注册句柄");

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

// V1.1.2 布局规则：右下角锚定、尺寸钳制、只开左/上/左上的调整命中区。
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

// V2.0.0 权重与光圈。
var now = DateTimeOffset.UtcNow;
Equal(1.0, DockWeight.Decay(1.0, now, now, 7), "未经过时间不衰减");
True(Math.Abs(DockWeight.Decay(1.0, now.AddDays(-7), now, 7) - 0.5) < 1e-6, "一个半衰期衰减到一半");
True(DockWeight.Decay(1.0, now.AddDays(-14), now, 7) < 0.26, "两个半衰期继续衰减");
True(DockWeight.Accumulate(1.0, now.AddDays(-7), now, DockWeight.ClickWeight, 7) > 1.4, "衰减后再累加一次点击");
Equal(0.0, DockWeight.GlowOpacity(0, 0), "无权重不发光");
Equal(DockWeight.MaxGlowOpacity, DockWeight.GlowOpacity(5, 5), "最高权重光圈最亮");
True(DockWeight.GlowOpacity(1, 5) < DockWeight.GlowOpacity(4, 5), "权重越高光圈越亮");
Equal(DockWeight.MaxGlowBlur, DockWeight.GlowBlur(5, 5), "最高权重光圈最大");

// 只统计项目主文件夹：子目录不得计入。
True(RecentFolders.SamePath(@"C:\A\B", @"c:\a\b\"), "路径比较忽略大小写与尾斜杠");
True(!RecentFolders.SamePath(@"C:\A\B", @"C:\A\B\sub"), "子文件夹不得视为项目主文件夹");
Equal("2026-022-WBall", RecentFolders.StripCopySuffix("2026-022-WBall (2)"), "去掉重名副本后缀");

var policy = new DockPolicy { MinItems = 0, MaxItems = 999, HalfLifeDays = 0 }.Normalized();
True(policy.MinItems >= DockPolicy.LowestItems, "最少显示数下限");
True(policy.MaxItems <= DockPolicy.HighestItems, "最多显示数上限");
True(policy.HalfLifeDays >= DockPolicy.ShortestHalfLifeDays, "半衰期下限");
Equal("dock.manager", DockManagerView.CreateDescriptor().Id, "管理页面窗口 ID");

Console.WriteLine(
    "ActiveDock.Smoke: PASS (1 module, 12 commands, 1 UI module, shell-hosted manager, bottom-right layout, weight+glow)");

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

/// <summary>记录注册行为，供宿主判据断言。</summary>
file sealed class RecordingRegistrar : IShellUiRegistrar
{
    public List<string> Registered { get; } = [];

    public int Disposed { get; private set; }

    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(AppShell.Core.Docking.ToolWindowDescriptor descriptor, string owner)
    {
        Registered.Add(descriptor.Id);
        return new Handle(() => Disposed++);
    }

    public void UnregisterToolWindow(string id)
    {
    }

    public void UnregisterOwner(string owner)
    {
    }

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
