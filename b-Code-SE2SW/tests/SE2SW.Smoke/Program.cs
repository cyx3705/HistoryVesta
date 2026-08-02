using SE2SW;
using SE2SW.Contracts;
using SE2SW.Worker;
using AppShell.Core.Docking;
using AppShell.Core.Modules;
using System.Text.Json;
using System.Windows.Threading;

var root = Path.Combine(Path.GetTempPath(), "se2sw-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    TestOhsLayoutAndScan(root);
    TestViewModelDirectorySelection(root);
    TestMissingUnusedPreflight(root);
    TestExternalMapping(root);
    TestExternalLegacyAndDirectoryCreation(root);
    TestDuplicateOutputRejection(root);
    TestAssemblyPlanningAndJson(root);
    TestAssemblyLegacyReuse(root);
    TestAssemblyDuplicateNames(root);
    TestAssemblyRetryReuse(root);
    TestAssemblyRetryRejectsUnsafeOutputs(root);
    TestAssemblyTemplateFallback(root);
    TestAssemblyMatrixMapping();
    TestAssemblyTree();
    TestAssemblyViewModelState(root);
    TestAssemblyActiveRunDisposal(root);
    TestWorkspaceModes();
    TestUiModuleRegistration();
    TestTemporaryOutput(root);
    TestParasolidTextProbe(root);
    TestFeatureRecognitionRetries();
    TestFeatureRecognitionSessionGuards();
    TestActiveRunDisposal(root);
    Console.WriteLine("SE2SW.Smoke: PASS");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void TestOhsLayoutAndScan(string root)
{
    var project = Path.Combine(root, "2026-900-Smoke");
    var source = Path.Combine(project, "b-Module-SE");
    var unusedGe = Path.Combine(project, "Unused", "b-Module-GE");
    var unusedSw = Path.Combine(project, "Unused", "b-Module-SW");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(unusedGe);
    Directory.CreateDirectory(unusedSw);
    File.WriteAllText(Path.Combine(source, "A.par"), "sample-a");
    File.WriteAllText(Path.Combine(source, "B.PAR"), "sample-b");
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    File.WriteAllText(Path.Combine(source, "nested", "ignored.par"), "nested");

    var layout = OhsProjectResolver.Resolve(project);
    Equal(2, OhsProjectResolver.GetRequiredMoves(layout).Count, "OHS 应同时计划 GE/SW 两次移动");
    Equal(2, FileScanner.Scan(ConversionMode.Ohs, project, layout).Count, "扫描必须只包含顶层 par");

    OhsProjectResolver.PrepareOutputDirectories(layout);
    True(Directory.Exists(layout.XtDirectory), "GE 目录应移动到项目根");
    True(Directory.Exists(layout.SolidWorksDirectory), "SW 目录应移动到项目根");
    True(!Directory.Exists(unusedGe) && !Directory.Exists(unusedSw), "Unused 候选目录应被剪切");

    File.WriteAllText(Path.Combine(layout.XtDirectory, "A.x_t"), "existing");
    var rescanned = FileScanner.Scan(ConversionMode.Ohs, project, layout);
    True(rescanned.Single(item => Path.GetFileName(item.SourcePath) == "A.par").HasExistingOutput,
        "存在 XT 时应标记输出冲突");
}

static void TestMissingUnusedPreflight(string root)
{
    var project = Path.Combine(root, "2026-901-Missing");
    var source = Path.Combine(project, "b-Module-SE");
    var unusedGe = Path.Combine(project, "Unused", "b-Module-GE");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(unusedGe);
    var layout = OhsProjectResolver.Resolve(project);

    Throws<DirectoryNotFoundException>(() => OhsProjectResolver.GetRequiredMoves(layout));
    True(Directory.Exists(unusedGe), "双目录预检失败时不得提前移动 GE");
    True(!Directory.Exists(layout.XtDirectory), "双目录预检失败时项目根不得出现 GE");
}

static void TestViewModelDirectorySelection(string root)
{
    var project = Path.Combine(root, "2026-902-ViewModel");
    var source = Path.Combine(project, "b-Module-SE");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "OhsPart.par"), "ohs");

    using var projectSelection = CreateViewModel();
    projectSelection.SetDirectory(project);
    True(projectSelection.IsOhsMode, "选择 OHS 项目根时必须保持 OHS 模式");
    Equal(1, projectSelection.Files.Count, "选择 OHS 项目根后应立即显示零件");

    using var sourceSelection = CreateViewModel();
    sourceSelection.SetDirectory(source);
    True(sourceSelection.IsOhsMode, "直接选择 b-Module-SE 时必须保持 OHS 模式");
    Equal(project, sourceSelection.SelectedDirectory, "直接选择 b-Module-SE 时应回推项目根");
    Equal(1, sourceSelection.Files.Count, "直接选择 b-Module-SE 后应立即显示零件");

    var external = Path.Combine(root, "external-view-model");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "ExternalPart.PAR"), "external");
    using var externalSelection = CreateViewModel();
    externalSelection.SetDirectory(external);
    True(externalSelection.IsExternalMode, "默认 OHS 模式选择普通零件目录时应自动切换外界模式");
    Equal(1, externalSelection.Files.Count, "自动切换外界模式后应立即显示零件");
    True(externalSelection.StatusText.Contains("已切换到零件转换模式", StringComparison.Ordinal),
        "自动切换后状态栏应明确说明当前模式");

    using var manualScan = CreateViewModel();
    manualScan.SelectedDirectory = external;
    manualScan.Scan();
    True(manualScan.IsExternalMode, "手动填写普通零件目录后重新扫描也应自动切换外界模式");
    Equal(1, manualScan.Files.Count, "重新扫描应恢复普通目录中的零件列表");

    var invalidOhs = Path.Combine(root, "invalid-ohs");
    Directory.CreateDirectory(invalidOhs);
    using var invalidSelection = CreateViewModel();
    invalidSelection.SetDirectory(invalidOhs);
    True(invalidSelection.IsOhsMode, "空的非 OHS 目录不能被误判为外界零件目录");
    True(invalidSelection.StatusText.Contains("普通零件目录请切换到零件转换", StringComparison.Ordinal),
        "OHS 扫描失败时应给出模式修正提示");
}

static SE2SWViewModel CreateViewModel()
    => new(
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);

static void TestExternalMapping(string root)
{
    var external = Path.Combine(root, "external");
    Directory.CreateDirectory(external);
    var source = Path.Combine(external, "Outside.par");
    File.WriteAllText(source, "external");
    var item = FileScanner.Scan(ConversionMode.External, external).Single();
    Equal(Path.Combine(external, "XT", "Outside.x_t"), item.XtPath, "外界模式 XT 应进入 XT 子目录");
    Equal(Path.Combine(external, "SW", "Outside.SLDPRT"), item.SolidWorksPath, "外界模式 SW 应进入 SW 子目录");
    True(!Directory.Exists(Path.Combine(external, "XT")) && !Directory.Exists(Path.Combine(external, "SW")),
        "扫描阶段不得创建 XT/SW 目录");
}

static void TestExternalLegacyAndDirectoryCreation(string root)
{
    var external = Path.Combine(root, "external-legacy");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "Legacy.par"), "source");
    File.WriteAllText(Path.Combine(external, "Legacy.SLDPRT"), "legacy");
    True(FileScanner.Scan(ConversionMode.External, external).Single().HasExistingOutput,
        "旧平铺产物必须继续被识别，避免重复转换");

    var clean = Path.Combine(root, "external-create-on-convert");
    Directory.CreateDirectory(clean);
    ExternalOutputLayout.EnsureDirectories(clean);
    True(Directory.Exists(Path.Combine(clean, "XT")) && Directory.Exists(Path.Combine(clean, "SW")),
        "执行转换前应创建 XT/SW 目录");

    var conflict = Path.Combine(root, "external-directory-conflict");
    Directory.CreateDirectory(conflict);
    File.WriteAllText(Path.Combine(conflict, "SW"), "occupied");
    Throws<IOException>(() => ExternalOutputLayout.EnsureDirectories(conflict));
    True(!Directory.Exists(Path.Combine(conflict, "XT")), "任一目录冲突时不得提前创建另一输出目录");
}

static void TestAssemblyPlanningAndJson(string root)
{
    var directory = Path.Combine(root, "assembly-plan");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var part = Path.Combine(directory, "Part.par");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(part, "part");
    var world = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0.1, 0.2, 0.3, 1,
    };
    var occurrence = new AssemblyOccurrence("Part:1", null, part, false, false, true, world, "隐藏件");
    var probe = new AssemblyProbeResult(assembly, [occurrence], [part], 0, 0, 1, 0, []);
    var json = JsonSerializer.Serialize(probe);
    var roundTrip = JsonSerializer.Deserialize<AssemblyProbeResult>(json)
        ?? throw new InvalidOperationException("装配 JSON 往返失败");
    Equal(16, roundTrip.Occurrences.Single().WorldTransform.Length, "JSON 往返必须保留 16 元素矩阵");

    var plan = AssemblyPlanner.Create(roundTrip);
    True(plan.CanConvert, "合法单件装配应通过规划门禁");
    Equal(Path.Combine(directory, "XT", "Part.x_t"), plan.Parts.Single().XtPath,
        "装配零件 XT 必须进入分层目录");
    Equal(Path.Combine(directory, "SW", "Part.SLDPRT"), plan.Parts.Single().SolidWorksPath,
        "装配零件 SLDPRT 必须进入分层目录");
    Equal(Path.Combine(directory, "SW", "Top.SLDASM"), plan.AssemblyOutputPath,
        "SLDASM 必须与零件放在 SW 目录");
    True(plan.Warnings.Any(item => item.Contains("隐藏实例", StringComparison.Ordinal)),
        "隐藏件必须保留为警告");
    True(!Directory.Exists(plan.XtDirectory) && !Directory.Exists(plan.SolidWorksDirectory),
        "装配规划阶段不得创建输出目录");

    var duplicateOccurrence = occurrence with { OccurrenceId = "Part:2", WorldTransform = (double[])world.Clone() };
    var repeated = AssemblyPlanner.Create(probe with { Occurrences = [occurrence, duplicateOccurrence] });
    Equal(1, repeated.Parts.Count, "重复实例只能产生一个唯一零件转换任务");
    Equal(2, repeated.Occurrences.Count(item => !item.IsSubAssembly), "重复实例必须全部保留用于插入");
}

static void TestAssemblyLegacyReuse(string root)
{
    var directory = Path.Combine(root, "assembly-legacy-reuse");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var part = Path.Combine(directory, "Legacy.par");
    var legacyXt = Path.Combine(directory, "Legacy.x_t");
    var legacySw = Path.Combine(directory, "Legacy.SLDPRT");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(part, "part");
    File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddMinutes(-5));
    WriteValidXt(legacyXt);
    File.WriteAllText(legacySw, "legacy-solidworks-part");
    File.SetLastWriteTimeUtc(legacyXt, DateTime.UtcNow.AddMinutes(-2));
    File.SetLastWriteTimeUtc(legacySw, DateTime.UtcNow.AddMinutes(-2));
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        assembly,
        [new AssemblyOccurrence("Legacy:1", null, part, false, false, false, identity, null)],
        [part],
        0, 0, 1, 0, []);

    var plan = AssemblyPlanner.Create(probe);
    True(plan.CanConvert, "旧平铺产物不应阻止装配安全重试");
    Equal(legacyXt, plan.Parts.Single().XtPath, "旧平铺 XT 必须作为复用输入");
    Equal(legacySw, plan.Parts.Single().SolidWorksPath, "旧平铺 SLDPRT 必须直接参与组装");
    var job = new ConversionJob("legacy", part, plan.Parts.Single().XtPath, plan.Parts.Single().SolidWorksPath);
    Equal(1, AssemblyPartReusePlanner.Create([job], CancellationToken.None).ReusableSolidWorksParts.Count,
        "Worker 必须把有效旧平铺 SLDPRT 分类为直接复用");
}

static void TestAssemblyDuplicateNames(string root)
{
    var directory = Path.Combine(root, "assembly-duplicate");
    var a = Path.Combine(directory, "A");
    var b = Path.Combine(directory, "B");
    Directory.CreateDirectory(a);
    Directory.CreateDirectory(b);
    var assembly = Path.Combine(directory, "Top.asm");
    var partA = Path.Combine(a, "Same.par");
    var partB = Path.Combine(b, "Same.PAR");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(partA, "a");
    File.WriteAllText(partB, "b");
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        assembly,
        [
            new AssemblyOccurrence("A", null, partA, false, false, false, identity, null),
            new AssemblyOccurrence("B", null, partB, false, false, false, identity, null),
        ],
        [partA, partB],
        0, 0, 2, 0, []);
    var plan = AssemblyPlanner.Create(probe);
    True(!plan.CanConvert, "同名不同路径零件必须阻止转换");
    True(plan.BlockingIssues.Any(issue => issue.ErrorClass == ConversionErrorClass.DuplicateOutputName),
        "同名冲突必须归类为 DuplicateOutputName");
}

static void TestAssemblyRetryReuse(string root)
{
    var directory = Path.Combine(root, "assembly-retry");
    var xtDirectory = Path.Combine(directory, "XT");
    var swDirectory = Path.Combine(directory, "SW");
    Directory.CreateDirectory(xtDirectory);
    Directory.CreateDirectory(swDirectory);
    var assembly = Path.Combine(directory, "Top.asm");
    File.WriteAllText(assembly, "asm");

    var sourceTime = DateTime.UtcNow.AddMinutes(-5);
    var reuseSw = CreatePartJob("reuse-sw", "ReuseSw");
    var reuseXt = CreatePartJob("reuse-xt", "ReuseXt");
    var fresh = CreatePartJob("fresh", "Fresh");
    File.WriteAllText(reuseSw.SolidWorksPath, "solidworks-part");
    File.SetLastWriteTimeUtc(reuseSw.SolidWorksPath, sourceTime.AddMinutes(2));
    WriteValidXt(reuseXt.XtPath);
    File.SetLastWriteTimeUtc(reuseXt.XtPath, sourceTime.AddMinutes(2));

    var plan = AssemblyPartReusePlanner.Create([reuseSw, reuseXt, fresh], CancellationToken.None);
    Equal(1, plan.ReusableSolidWorksParts.Count, "已有有效 SLDPRT 必须直接复用");
    Equal("reuse-sw", plan.ReusableSolidWorksParts.Single().Id, "SLDPRT 复用任务分类错误");
    Equal(1, plan.ImportFromExistingXt.Count, "只有有效 XT 时必须跳过 SE 导出并进入 SW 导入");
    Equal("reuse-xt", plan.ImportFromExistingXt.Single().Id, "XT 复用任务分类错误");
    Equal(1, plan.NeedsExport.Count, "没有产物的零件必须走完整转换");

    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var request = new AssemblyBatchRequest(
        "retry-smoke",
        ConversionMode.External,
        assembly,
        Path.Combine(swDirectory, "Top.SLDASM"),
        [reuseSw, reuseXt, fresh],
        [
            new AssemblyOccurrence("A", null, reuseSw.SourcePath, false, false, false, identity, null),
            new AssemblyOccurrence("B", null, reuseXt.SourcePath, false, false, false, identity, null),
            new AssemblyOccurrence("C", null, fresh.SourcePath, false, false, false, identity, null),
        ]);
    PreflightValidator.ValidateAssemblyRequest(request);
    WorkerRequestValidator.Validate(request);
    File.WriteAllText(request.AssemblyOutputPath, "existing-assembly");
    Throws<IOException>(() => PreflightValidator.ValidateAssemblyRequest(request));
    Throws<IOException>(() => WorkerRequestValidator.Validate(request));

    ConversionJob CreatePartJob(string id, string name)
    {
        var source = Path.Combine(directory, name + ".par");
        File.WriteAllText(source, "part");
        File.SetLastWriteTimeUtc(source, sourceTime);
        return new ConversionJob(
            id,
            source,
            Path.Combine(xtDirectory, name + ".x_t"),
            Path.Combine(swDirectory, name + ".SLDPRT"));
    }
}

static void TestAssemblyRetryRejectsUnsafeOutputs(string root)
{
    var directory = Path.Combine(root, "assembly-retry-invalid");
    Directory.CreateDirectory(directory);
    var sourceTime = DateTime.UtcNow.AddMinutes(-2);

    var emptySw = CreateJob("empty-sw");
    File.WriteAllBytes(emptySw.SolidWorksPath, []);
    File.SetLastWriteTimeUtc(emptySw.SolidWorksPath, sourceTime.AddMinutes(1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([emptySw], CancellationToken.None));

    var staleSw = CreateJob("stale-sw");
    File.WriteAllText(staleSw.SolidWorksPath, "old-part");
    File.SetLastWriteTimeUtc(staleSw.SolidWorksPath, sourceTime.AddMinutes(-1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([staleSw], CancellationToken.None));

    var invalidXt = CreateJob("invalid-xt");
    File.WriteAllText(invalidXt.XtPath, "not-parasolid");
    File.SetLastWriteTimeUtc(invalidXt.XtPath, sourceTime.AddMinutes(1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([invalidXt], CancellationToken.None));

    var staleXt = CreateJob("stale-xt");
    WriteValidXt(staleXt.XtPath);
    File.SetLastWriteTimeUtc(staleXt.XtPath, sourceTime.AddMinutes(-1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([staleXt], CancellationToken.None));

    ConversionJob CreateJob(string name)
    {
        var source = Path.Combine(directory, name + ".par");
        File.WriteAllText(source, "part");
        File.SetLastWriteTimeUtc(source, sourceTime);
        return new ConversionJob(
            name,
            source,
            Path.Combine(directory, name + ".x_t"),
            Path.Combine(directory, name + ".SLDPRT"));
    }
}

static void TestAssemblyTemplateFallback(string root)
{
    var programData = Path.Combine(root, "template-fallback");
    var templates = Path.Combine(programData, "SOLIDWORKS", "SOLIDWORKS 2025", "templates");
    Directory.CreateDirectory(templates);
    var standard = Path.Combine(templates, "gb_assembly.asmdot");
    var secondary = Path.Combine(templates, "z_assembly.asmdot");
    File.WriteAllText(standard, "standard");
    File.WriteAllText(secondary, "secondary");

    var fallback = SolidWorksAssemblyTemplateResolver.FindCandidates(
        "~BLANK_ASSY_TEMPLATE.asmdot",
        installDirectory: null,
        commonApplicationData: programData);
    Equal(standard, fallback.First(), "虚拟默认模板必须回退到标准物理 gb_assembly.asmdot");

    var configured = Path.Combine(programData, "configured.asmdot");
    File.WriteAllText(configured, "configured");
    var configuredFirst = SolidWorksAssemblyTemplateResolver.FindCandidates(
        configured,
        installDirectory: null,
        commonApplicationData: programData);
    Equal(configured, configuredFirst.First(), "有效的当前默认模板必须保持最高优先级");
}

static void WriteValidXt(string path)
    => File.WriteAllText(
        path,
        "**ABCDEFGHIJKLMNOPQRSTUVWXYZ**;\r\nFORMAT=text;\r\nmodeller version SCH_3101255_31100_1300;\r\n");

static void TestAssemblyMatrixMapping()
{
    var world = new[]
    {
        0d, -1, 0, 0,
        1, 0, 0, 0,
        0, 0, 1, 0,
        0.4, -0.5, 0.6, 1,
    };
    var mapped = SolidWorksAssemblyBuilder.ToSolidWorksTransform(world);
    Equal(16, mapped.Length, "SolidWorks MathTransform 必须是 16 元素");
    Equal(-1d, mapped[1], "旋转矩阵不得转置");
    Equal(1d, mapped[3], "旋转矩阵不得转置");
    Equal(0.4d, mapped[9], "平移 X 必须从 SE[12] 映射到 SW[9]");
    Equal(-0.5d, mapped[10], "平移 Y 必须从 SE[13] 映射到 SW[10]");
    Equal(0.6d, mapped[11], "平移 Z 必须从 SE[14] 映射到 SW[11]");
    Equal(1d, mapped[12], "SolidWorks 变换缩放必须为 1");
}

static void TestAssemblyTree()
{
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        @"C:\fixture\Top.asm",
        [
            new AssemblyOccurrence("Sub:1", null, @"C:\fixture\Sub.asm", true, false, false, identity, null),
            new AssemblyOccurrence("Sub:1/Part:1", "Sub:1", @"C:\fixture\Part.par", false, false, false, identity, null),
        ],
        [@"C:\fixture\Part.par"],
        0, 0, 1, 0, []);
    var rootNode = AssemblyTreeNode.Build(probe);
    Equal(1, rootNode.Children.Count, "装配树应有一个顶层子装配");
    Equal(1, rootNode.Children[0].Children.Count, "ParentId 必须还原真实子层级");
    Equal("Part:1", rootNode.Children[0].Children[0].DisplayName, "树节点应显示 occurrence 名称");
}

static void TestUiModuleRegistration()
{
    var registrar = new RecordingShellUiRegistrar();
    var module = new SE2SWUiModule { ShellUi = registrar };
    module.CreateUi();
    Equal(1, registrar.Descriptors.Count, "模块应只注册一个双页工具窗口");
    var descriptor = registrar.Descriptors.Single();
    Equal("se2sw", descriptor.Id, "必须保留稳定窗口 ID se2sw");
    Equal("SE2SW 转换", descriptor.Title, "窗口标题应覆盖零件与装配两种转换");
    True(descriptor.ContentFactory != null, "双页工具窗口必须提供内容工厂");
    Equal(DockSide.Right, descriptor.DefaultSide, "窗口应保持 AppShell 普通右侧工具窗口语义");
    module.DestroyUi();
    Equal(1, registrar.DisposeCount, "热卸载必须释放双页窗口句柄");
}

static void TestWorkspaceModes()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var workspace = new SE2SWWorkspaceView();
            Equal(3, workspace.PageCount, "V3.1 工作区必须只有三个同级模式");
            Equal(0, workspace.SelectedPageIndex, "默认必须进入模式 1：零件转换");
            Equal(ConversionMode.External, workspace.ActivePartMode, "零件转换必须对应原外界模式");

            workspace.SelectedPageIndex = 1;
            Equal(1, workspace.SelectedPageIndex, "模式 2 必须进入装配转换");

            workspace.SelectedPageIndex = 2;
            Equal(ConversionMode.Ohs, workspace.ActivePartMode, "模式 3 必须对应 OHS 零件兼容模式");

            workspace.PartPage.SetMode(ConversionMode.External);
            Equal(0, workspace.SelectedPageIndex, "零件页自动识别外界目录后顶层模式必须同步回零件转换");

            Equal(ConversionMode.External, workspace.ActivePartMode, "返回模式 1 必须恢复外界零件模式");
            Throws<ArgumentOutOfRangeException>(() => workspace.SelectedPageIndex = 3);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
        throw new InvalidOperationException("三模式工作区 Smoke 失败。", failure);
}

static void TestAssemblyViewModelState(string root)
{
    var directory = Path.Combine(root, "assembly-view-model");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var changedAssembly = Path.Combine(directory, "Changed.asm");
    var part = Path.Combine(directory, "Part.par");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(changedAssembly, "changed");
    File.WriteAllText(part, "part");
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        assembly,
        [new AssemblyOccurrence("Part:1", null, part, false, false, false, identity, null)],
        [part],
        0, 0, 1, 0, []);
    using var viewModel = new AssemblyViewModel(
        (_, _, _) => Task.FromResult(probe),
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);
    viewModel.SetSourceFile(assembly);
    True(viewModel.CanProbe && !viewModel.CanConvert, "选择 .asm 后只能先解析，不能直接转换");
    viewModel.ProbeAsync().GetAwaiter().GetResult();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    True(viewModel.CanConvert, "解析成功且前置检查通过后才允许转换");
    Equal(1, viewModel.Parts.Count, "装配窗口应显示唯一零件清单");
    Equal(1, viewModel.AssemblyTree.Count, "装配窗口应显示真实层级根节点");
    True(viewModel.WarningSummary.Contains("展平", StringComparison.Ordinal), "装配窗口必须明确提示最终输出展平");
    True(!viewModel.RecognizeFeatures, "装配模式 FeatureWorks 必须默认关闭");
    True(!Directory.Exists(Path.Combine(directory, "XT")) && !Directory.Exists(Path.Combine(directory, "SW")),
        "装配窗口解析完成也不得创建输出目录");

    viewModel.SetSourceFile(changedAssembly);
    True(!viewModel.CanConvert && viewModel.Parts.Count == 0 && viewModel.AssemblyTree.Count == 0,
        "切换源文件必须清空旧探查结果并禁用转换");
}

static void TestAssemblyActiveRunDisposal(string root)
{
    var directory = Path.Combine(root, "assembly-dispose-running");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    File.WriteAllText(assembly, "asm");
    var workerStarted = new ManualResetEventSlim();
    var workerExited = new ManualResetEventSlim();
    var cancellationObserved = false;
    var viewModel = new AssemblyViewModel(
        async (_, _, cancellationToken) =>
        {
            workerStarted.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("不可达");
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                throw;
            }
            finally
            {
                await Task.Delay(100).ConfigureAwait(false);
                workerExited.Set();
            }
        },
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);
    viewModel.SetSourceFile(assembly);
    var run = viewModel.ProbeAsync();
    True(workerStarted.Wait(TimeSpan.FromSeconds(3)), "装配假 Worker 必须启动");
    var dispose = Task.Run(viewModel.Dispose);
    Thread.Sleep(25);
    True(!dispose.IsCompleted, "装配 ViewModel Dispose 必须等待 Worker 收束");
    True(dispose.Wait(TimeSpan.FromSeconds(3)), "装配 ViewModel 必须在取消完成后释放");
    True(cancellationObserved && workerExited.IsSet, "装配生命周期必须传播取消并等待 Worker 退出");
    True(run.IsCompletedSuccessfully, "Dispose 返回前装配操作任务必须完成");
    Throws<ObjectDisposedException>(() => viewModel.ProbeAsync().GetAwaiter().GetResult());
}

static void TestDuplicateOutputRejection(string root)
{
    var external = Path.Combine(root, "duplicates");
    Directory.CreateDirectory(external);
    var sourceA = Path.Combine(external, "A.par");
    var sourceB = Path.Combine(external, "B.par");
    File.WriteAllText(sourceA, "a");
    File.WriteAllText(sourceB, "b");
    var duplicateXt = Path.Combine(external, "same.x_t");
    var jobs = new[]
    {
        new ConversionJob("a", sourceA, duplicateXt, Path.Combine(external, "A.SLDPRT")),
        new ConversionJob("b", sourceB, duplicateXt, Path.Combine(external, "B.SLDPRT")),
    };
    Throws<InvalidDataException>(() => PreflightValidator.ValidateJobs(jobs, overwrite: false));
}

static void TestTemporaryOutput(string root)
{
    var finalPath = Path.Combine(root, "atomic.SLDPRT");
    var temporaryPath = TemporaryOutput.For(finalPath);
    True(Path.GetDirectoryName(temporaryPath) == root, "临时输出必须与正式输出同目录");
    True(temporaryPath.EndsWith(".tmp.SLDPRT", StringComparison.OrdinalIgnoreCase),
        "临时输出必须保留 CAD 可识别的最终扩展名");
    File.WriteAllText(temporaryPath, "stable-output");
    TemporaryOutput.Commit(temporaryPath, finalPath);
    True(File.Exists(finalPath) && !File.Exists(temporaryPath), "提交必须把临时输出原子移动到正式路径");
}

static void TestParasolidTextProbe(string root)
{
    var textPath = Path.Combine(root, "probe.x_t");
    File.WriteAllText(textPath, "**ABCDEFGHIJKLMNOPQRSTUVWXYZ**;\r\nFORMAT=text;\r\nmodeller version SCH_3101255_31100_1300;\r\n");
    var facts = FileProbe.VerifyParasolidText(textPath, CancellationToken.None, TimeSpan.FromSeconds(3));
    Equal("text", facts.ParasolidFormat, "必须识别 Parasolid 文本格式头");
    Equal("SCH_3101255_31100_1300", facts.ParasolidSchema, "必须提取 Parasolid schema");

    var binaryPath = Path.Combine(root, "probe-binary.x_t");
    File.WriteAllText(binaryPath, "FORMAT=binary;\r\n");
    Throws<InvalidDataException>(() =>
        FileProbe.VerifyParasolidText(binaryPath, CancellationToken.None, TimeSpan.FromSeconds(3)));
}

static void TestFeatureRecognitionRetries()
{
    var selections = 0;
    var recognitions = 0;
    var waits = 0;
    var recovered = FeatureRecognizer.RunRecognitionAttempts(
        () =>
        {
            selections++;
            return null;
        },
        () => ++recognitions < 3 ? 0 : 12,
        () => waits++,
        CancellationToken.None);

    Equal(12, recovered.RecognizedFeatureCount, "异步就绪后应返回识别出的特征数");
    Equal(3, recovered.Attempts, "前两次返回 0 时应继续尝试首件识别");
    Equal(3, selections, "每次识别前都必须重新选择种子面");
    Equal(2, waits, "返回 0 后应等待并泵送消息，上次成功后不再等待");

    selections = 0;
    waits = 0;
    var exhausted = FeatureRecognizer.RunRecognitionAttempts(
        () =>
        {
            selections++;
            return null;
        },
        static () => 0,
        () => waits++,
        CancellationToken.None);

    Equal(0, exhausted.RecognizedFeatureCount, "达到重试上限后才允许按未识别降级");
    Equal(10, exhausted.Attempts, "识别重试必须有确定的上限");
    Equal(10, selections, "每次重试都必须刷新种子面选择");
    Equal(9, waits, "最后一次失败后不应再等待");
}

static void TestFeatureRecognitionSessionGuards()
{
    True(
        FeatureRecognizer.DocumentTitlesMatch("ImportedPart", "importedpart"),
        "活动文档标题比较应忽略大小写");
    True(
        !FeatureRecognizer.DocumentTitlesMatch("ImportedPart", "UserAssembly"),
        "不得把 FeatureWorks 命令发给用户原有文档");

    var empty = new FeatureOutcome(0, false, 0, 0, [], true, 100, "recognition returned 0");
    var success = new FeatureOutcome(4, true, 1, 1, [], false, 100);
    True(
        SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, true, empty),
        "首件首次识别为 0 时应重新导入一次");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(0, 1, true, empty),
        "首件恢复只能执行一次");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(1, 0, true, empty),
        "后续零件识别为 0 时不得套用首件初始化恢复");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, false, empty),
        "FeatureWorks 不可用时重新导入没有意义");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, true, success),
        "首件识别成功时不得重新导入");
}

static void TestActiveRunDisposal(string root)
{
    var external = Path.Combine(root, "dispose-running");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "Running.par"), "sample");

    var workerStarted = new ManualResetEventSlim();
    var workerExited = new ManualResetEventSlim();
    var cancellationObserved = false;
    var viewModel = new SE2SWViewModel(
        async (_, progress, cancellationToken) =>
        {
            workerStarted.Set();
            progress(new WorkerEvent(
                "smoke",
                null,
                ConversionStage.SolidEdgeExport,
                "queued progress"));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                throw;
            }
            finally
            {
                await Task.Delay(100).ConfigureAwait(false);
                workerExited.Set();
            }
        },
        static () => { },
        Dispatcher.CurrentDispatcher);

    viewModel.SetMode(ConversionMode.External);
    viewModel.SetDirectory(external);
    var run = viewModel.StartAsync();
    True(workerStarted.Wait(TimeSpan.FromSeconds(3)), "假 Worker 必须启动");

    var dispose = Task.Run(viewModel.Dispose);
    Thread.Sleep(25);
    True(!dispose.IsCompleted, "Dispose 必须等待 Worker 收束，不能只发送取消");
    True(dispose.Wait(TimeSpan.FromSeconds(3)), "Dispose 必须在 Worker 收束后返回");
    True(cancellationObserved, "Dispose 必须取消活动批次");
    True(workerExited.IsSet, "Dispose 返回前 Worker 必须已经退出");
    True(run.IsCompletedSuccessfully, "Dispose 返回前 StartAsync 必须完成");
    Dispatcher.CurrentDispatcher.Invoke(static () => { });
    True(viewModel.StatusText != "queued progress", "Dispose 必须撤销尚未执行的 UI 进度回调");
    Throws<ObjectDisposedException>(() => viewModel.StartAsync().GetAwaiter().GetResult());
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}。Expected={expected}, Actual={actual}");
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected exception {typeof(T).Name}");
}

sealed class RecordingShellUiRegistrar : IShellUiRegistrar
{
    public List<ToolWindowDescriptor> Descriptors { get; } = [];
    public int DisposeCount { get; private set; }
    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Descriptors.Add(descriptor);
        return new CallbackDisposable(() => DisposeCount++);
    }

    public void UnregisterToolWindow(string id)
    {
    }

    public void UnregisterOwner(string owner)
    {
    }
}

sealed class CallbackDisposable(Action callback) : IDisposable
{
    private Action? _callback = callback;

    public void Dispose()
        => Interlocked.Exchange(ref _callback, null)?.Invoke();
}
