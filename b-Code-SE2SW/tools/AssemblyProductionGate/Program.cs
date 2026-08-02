using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SE2SW.Contracts;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AssemblyProductionGate;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--set-template", StringComparison.OrdinalIgnoreCase))
            return SetAssemblyTemplate(args[1]);
        if (args.Length == 1 && string.Equals(args[0], "--cleanup-gate-documents", StringComparison.OrdinalIgnoreCase))
            return CleanupGateDocuments();

        GateOptions options;
        try
        {
            options = GateOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("用法：AssemblyProductionGate --se-asm <绝对.asm> --worker <SE2SW.Worker.exe> --assembly-template <物理.asmdot>；或追加 --reuse-existing 测试安全重试与模板回退");
            return 2;
        }

        var result = new GateResult
        {
            SourceAssembly = options.SourceAssembly,
            WorkerPath = options.WorkerPath,
            PhysicalAssemblyTemplate = options.PhysicalAssemblyTemplate ?? string.Empty,
        };
        try
        {
            Run(options, result);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.Success ? 0 : 1;
    }

    private static int SetAssemblyTemplate(string template)
    {
        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            var previous = application.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            if (!application.SetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                    template))
            {
                throw new InvalidOperationException("SolidWorks 拒绝设置默认装配模板。");
            }
            Console.WriteLine($"SolidWorks 装配模板：{previous} -> {template}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
            {
                var owned = GetPids("SLDWORKS").Except(before).ToArray();
                if (owned.Length > 0)
                {
                    try { application.ExitApp(); } catch { }
                }
                Release(application);
            }
        }
    }

    private static int CleanupGateDocuments()
    {
        var tempGateRoot = Path.GetFullPath(Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "SE2SW-V300"));
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            ModelDoc2? document = application.GetFirstDocument() as ModelDoc2;
            var closed = 0;
            while (document is not null)
            {
                ModelDoc2? next = null;
                try
                {
                    next = document.GetNext() as ModelDoc2;
                    var path = document.GetPathName();
                    if (!string.IsNullOrWhiteSpace(path)
                        && Path.GetFullPath(path).StartsWith(tempGateRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var title = document.GetTitle();
                        application.CloseDoc(title);
                        Console.WriteLine($"已关闭门禁文档：{path}");
                        closed++;
                    }
                }
                finally
                {
                    Release(document);
                }
                document = next;
            }
            Console.WriteLine($"门禁文档清理完成：{closed} 个。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Release(application);
        }
    }

    private static void Run(GateOptions options, GateResult result)
    {
        ValidateInput(options);
        var sourceDirectory = Path.GetDirectoryName(options.SourceAssembly)!;
        var xtDirectory = Path.Combine(sourceDirectory, "XT");
        var swDirectory = Path.Combine(sourceDirectory, "SW");
        if (File.Exists(xtDirectory) || File.Exists(swDirectory))
            throw new IOException("XT 或 SW 被同名文件占用。");
        if (!options.ReuseExisting && (Directory.Exists(xtDirectory) || Directory.Exists(swDirectory)))
            throw new IOException("真机门禁拒绝复用已有 XT/SW，请使用干净 fixture，或显式传入 --reuse-existing。");
        if (options.ReuseExisting && (!Directory.Exists(xtDirectory) || !Directory.Exists(swDirectory)))
            throw new DirectoryNotFoundException("安全重试门禁要求 XT/SW 分层目录已经存在。");

        var edgeBefore = GetPids("Edge");
        var swBefore = GetPids("SLDWORKS");
        var gateDirectory = Directory.CreateTempSubdirectory("SE2SW-V300-ProductionGate-").FullName;
        result.GateDirectory = gateDirectory;
        var probeResultPath = Path.Combine(gateDirectory, "assembly-probe-result.json");
        var probeRequest = new AssemblyProbeRequest(
            "gate-probe-" + Guid.NewGuid().ToString("N"),
            options.SourceAssembly,
            probeResultPath);
        var probeRun = RunWorker(options.WorkerPath, "--probe-assembly", probeRequest.BatchId, probeRequest, gateDirectory);
        result.WorkerEvents.AddRange(probeRun.Events);
        if (probeRun.ExitCode is not (0 or 1) || !File.Exists(probeResultPath))
            throw new InvalidOperationException($"生产 Worker 装配探查失败，exit={probeRun.ExitCode}：{probeRun.StandardError}");
        var probe = JsonSerializer.Deserialize<AssemblyProbeResult>(File.ReadAllText(probeResultPath), JsonOptions)
            ?? throw new InvalidDataException("生产 Worker 装配探查结果为空。");
        result.OccurrenceCount = probe.Occurrences.Count;
        result.UniquePartCount = probe.UniquePartPaths.Count;
        result.SuppressedCount = probe.SuppressedCount;
        result.UnresolvedCount = probe.UnresolvedCount;
        if (probe.UnresolvedCount != 0)
            throw new InvalidDataException($"装配有 {probe.UnresolvedCount} 个未解析引用。");

        var partPaths = probe.Occurrences
            .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
            .Select(item => Path.GetFullPath(item.SourcePath))
            .Where(path => string.Equals(Path.GetExtension(path), ".par", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateNames = partPaths
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (duplicateNames.Length > 0)
            throw new InvalidDataException("门禁 fixture 存在同名不同路径零件。");
        if (partPaths.Length == 0)
            throw new InvalidDataException("门禁 fixture 没有可转换 .par 零件。");
        if (!options.ReuseExisting && (Directory.Exists(xtDirectory) || Directory.Exists(swDirectory)))
            throw new InvalidOperationException("探查阶段错误创建了 XT/SW 目录。");

        var sourceHashes = partPaths.Append(options.SourceAssembly)
            .ToDictionary(path => path, ComputeSha256, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(xtDirectory);
        Directory.CreateDirectory(swDirectory);
        var jobs = partPaths.Select((path, index) => new ConversionJob(
            "gate-part-" + (index + 1),
            path,
            Path.Combine(xtDirectory, Path.GetFileNameWithoutExtension(path) + ".x_t"),
            Path.Combine(swDirectory, Path.GetFileNameWithoutExtension(path) + ".SLDPRT"))).ToArray();
        var assemblyOutput = Path.Combine(
            swDirectory,
            Path.GetFileNameWithoutExtension(options.SourceAssembly) + ".SLDASM");
        if (File.Exists(assemblyOutput))
            throw new IOException($"装配输出已经存在，门禁不会覆盖：{assemblyOutput}");
        var existingPartOutputHashes = options.ReuseExisting
            ? jobs.SelectMany(job => new[] { job.XtPath, job.SolidWorksPath })
                .Where(File.Exists)
                .ToDictionary(path => path, ComputeSha256, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assemblyRequest = new AssemblyBatchRequest(
            "gate-build-" + Guid.NewGuid().ToString("N"),
            ConversionMode.External,
            options.SourceAssembly,
            assemblyOutput,
            jobs,
            probe.Occurrences,
            RecognizeFeatures: false,
            FullyDefineSketches: false,
            ContinueWhenPartFails: false);

        ISldWorks? application = null;
        string? originalTemplate = null;
        int[] ownedSwPids = [];
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            ownedSwPids = GetPids("SLDWORKS").Except(swBefore).ToArray();
            if (ownedSwPids.Length > 0)
            {
                application.Visible = false;
                application.UserControl = false;
            }
            originalTemplate = application.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            result.OriginalAssemblyTemplate = originalTemplate;
            if (options.PhysicalAssemblyTemplate is not null
                && !application.SetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                    options.PhysicalAssemblyTemplate))
            {
                throw new InvalidOperationException("无法临时设置 SolidWorks 默认装配模板。");
            }

            var buildRun = RunWorker(
                options.WorkerPath,
                "--assembly",
                assemblyRequest.BatchId,
                assemblyRequest,
                gateDirectory);
            result.WorkerEvents.AddRange(buildRun.Events);
            result.WorkerExitCode = buildRun.ExitCode;
            if (buildRun.ExitCode != 0)
                throw new InvalidOperationException($"生产 Worker 装配构建失败，exit={buildRun.ExitCode}：{buildRun.StandardError}");
            VerifyAssembly(application, assemblyOutput, probe.Occurrences, jobs, result);
        }
        finally
        {
            if (application is not null)
            {
                if (options.PhysicalAssemblyTemplate is not null
                    && originalTemplate is not null
                    && !application.SetUserPreferenceStringValue(
                        (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                        originalTemplate))
                {
                    result.TemplateRestored = false;
                }
                else
                {
                    result.TemplateRestored = true;
                }
                if (ownedSwPids.Length > 0)
                {
                    try { application.ExitApp(); } catch { }
                }
                Release(application);
            }
            WaitForProcessesToExit(ownedSwPids, TimeSpan.FromSeconds(20));
        }

        if (!result.TemplateRestored)
            throw new InvalidOperationException("SolidWorks 默认装配模板未能恢复。");
        foreach (var pair in sourceHashes)
        {
            if (!string.Equals(pair.Value, ComputeSha256(pair.Key), StringComparison.Ordinal))
                throw new InvalidDataException($"源 CAD 文件被修改：{pair.Key}");
        }
        result.SourceHashesUnchanged = true;
        foreach (var pair in existingPartOutputHashes)
        {
            if (!File.Exists(pair.Key)
                || !string.Equals(pair.Value, ComputeSha256(pair.Key), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"安全重试改写了已有零件产物：{pair.Key}");
            }
        }
        result.ExistingPartOutputsUnchanged = true;
        result.OutputFiles = Directory.EnumerateFiles(xtDirectory)
            .Concat(Directory.EnumerateFiles(swDirectory))
            .Select(path => new OutputFact(path, new FileInfo(path).Length, ComputeSha256(path)))
            .ToList();
        if (result.OutputFiles.Any(file => file.Length <= 0))
            throw new InvalidDataException("门禁产物存在空文件。");

        WaitForUnexpectedProcesses(edgeBefore, swBefore, TimeSpan.FromSeconds(20));
        result.EdgeProcessesAfter = GetPids("Edge");
        result.SolidWorksProcessesAfter = GetPids("SLDWORKS");
        if (!result.EdgeProcessesAfter.SequenceEqual(edgeBefore)
            || !result.SolidWorksProcessesAfter.SequenceEqual(swBefore))
        {
            throw new InvalidOperationException("真机门禁结束后存在新增 CAD 进程。");
        }
    }

    private static void VerifyAssembly(
        ISldWorks application,
        string outputPath,
        IReadOnlyList<AssemblyOccurrence> occurrences,
        IReadOnlyList<ConversionJob> jobs,
        GateResult result)
    {
        var errors = 0;
        var warnings = 0;
        ModelDoc2? model = null;
        try
        {
            model = application.OpenDoc6(
                outputPath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                string.Empty,
                ref errors,
                ref warnings);
            if (model is null || errors != 0)
                throw new InvalidDataException($"独立重开 SLDASM 失败：errors={errors}, warnings={warnings}");
            var assembly = (AssemblyDoc)model;
            var rawComponents = assembly.GetComponents(false) as Array ?? Array.Empty<object>();
            var actual = rawComponents.Cast<object>().OfType<Component2>().Select(ReadComponent).ToList();
            var bySource = jobs.ToDictionary(
                job => Path.GetFullPath(job.SourcePath),
                job => Path.GetFullPath(job.SolidWorksPath),
                StringComparer.OrdinalIgnoreCase);
            var expected = occurrences
                .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
                .Where(item => bySource.ContainsKey(Path.GetFullPath(item.SourcePath)))
                .Select(item => new ExpectedComponent(
                    bySource[Path.GetFullPath(item.SourcePath)],
                    ToSolidWorksTransform(item.WorldTransform)))
                .ToList();
            result.ComponentExpected = expected.Count;
            result.ComponentActual = actual.Count;
            if (actual.Count != expected.Count)
                throw new InvalidDataException($"重开后组件数不一致：{actual.Count}/{expected.Count}");

            var maxTranslation = 0d;
            var maxRotation = 0d;
            foreach (var expectedComponent in expected)
            {
                var candidates = actual
                    .Where(item => string.Equals(
                        Path.GetFullPath(item.Path),
                        expectedComponent.Path,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => MaxTransformDeviation(expectedComponent.Transform, item.Transform))
                    .ToArray();
                if (candidates.Length == 0)
                    throw new FileNotFoundException("SLDASM 组件引用未解析。", expectedComponent.Path);
                var matched = candidates[0];
                actual.Remove(matched);
                if (!matched.IsFixed)
                    throw new InvalidDataException($"组件未固定：{matched.Path}");
                maxRotation = Math.Max(maxRotation, MaxRotationDeviation(expectedComponent.Transform, matched.Transform));
                maxTranslation = Math.Max(maxTranslation, MaxTranslationDeviation(expectedComponent.Transform, matched.Transform));
            }
            result.ComponentFixed = expected.Count;
            result.MaxRotationDeviation = maxRotation;
            result.MaxTranslationDeviationMeters = maxTranslation;
            if (maxRotation >= 1e-9 || maxTranslation >= 1e-6)
                throw new InvalidDataException($"重开后组件变换超差：rotation={maxRotation:G6}, translation={maxTranslation:G6}m");
        }
        finally
        {
            if (model is not null)
            {
                try { application.CloseDoc(model.GetTitle()); } catch { }
                Release(model);
            }
        }
    }

    private static ActualComponent ReadComponent(Component2 component)
    {
        MathTransform? transform = null;
        try
        {
            transform = component.Transform2;
            if (transform?.ArrayData is not Array values || values.Length != 16)
                throw new InvalidDataException($"组件变换无效：{component.Name2}");
            var path = component.GetPathName();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("组件引用未解析。", path);
            return new ActualComponent(
                Path.GetFullPath(path),
                values.Cast<object>().Select(Convert.ToDouble).ToArray(),
                component.IsFixed());
        }
        finally
        {
            Release(transform);
            Release(component);
        }
    }

    private static WorkerRun RunWorker<TRequest>(
        string workerPath,
        string verb,
        string batchId,
        TRequest request,
        string gateDirectory)
    {
        var requestPath = Path.Combine(gateDirectory, batchId + ".json");
        var cancellationPath = Path.Combine(gateDirectory, batchId + ".cancel");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, JsonOptions));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(verb);
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add("--cancel");
        process.StartInfo.ArgumentList.Add(cancellationPath);
        if (!process.Start())
            throw new InvalidOperationException("无法启动生产 Worker。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        var events = stdout.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                try { return JsonSerializer.Deserialize<WorkerEvent>(line, JsonOptions); }
                catch { return null; }
            })
            .Where(item => item is not null)
            .Cast<WorkerEvent>()
            .ToArray();
        return new WorkerRun(process.ExitCode, events, stderr.Result.Trim());
    }

    private static double[] ToSolidWorksTransform(IReadOnlyList<double> source) =>
    [
        source[0], source[1], source[2],
        source[4], source[5], source[6],
        source[8], source[9], source[10],
        source[12], source[13], source[14],
        1, 0, 0, 0,
    ];

    private static double MaxRotationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
        => Enumerable.Range(0, 9).Max(index => Math.Abs(expected[index] - actual[index]));

    private static double MaxTranslationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
        => Enumerable.Range(9, 3).Max(index => Math.Abs(expected[index] - actual[index]));

    private static double MaxTransformDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
        => Math.Max(MaxRotationDeviation(expected, actual), MaxTranslationDeviation(expected, actual));

    private static string ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateInput(GateOptions options)
    {
        if (!File.Exists(options.SourceAssembly)
            || !string.Equals(Path.GetExtension(options.SourceAssembly), ".asm", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("源装配体不存在。", options.SourceAssembly);
        if (!File.Exists(options.WorkerPath))
            throw new FileNotFoundException("生产 Worker 不存在。", options.WorkerPath);
        if (options.PhysicalAssemblyTemplate is not null
            && (!File.Exists(options.PhysicalAssemblyTemplate)
                || !string.Equals(Path.GetExtension(options.PhysicalAssemblyTemplate), ".asmdot", StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException("物理装配模板不存在。", options.PhysicalAssemblyTemplate);
    }

    private static int[] GetPids(string processName)
        => Process.GetProcessesByName(processName)
            .Select(process => { using (process) return process.Id; })
            .OrderBy(id => id)
            .ToArray();

    private static void WaitForUnexpectedProcesses(int[] edgeBaseline, int[] swBaseline, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (GetPids("Edge").SequenceEqual(edgeBaseline) && GetPids("SLDWORKS").SequenceEqual(swBaseline))
                return;
            Thread.Sleep(200);
        }
    }

    private static void WaitForProcessesToExit(IEnumerable<int> processIds, TimeSpan timeout)
    {
        var ids = processIds.ToArray();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (ids.All(id => !IsAlive(id)))
                return;
            Thread.Sleep(200);
        }
    }

    private static bool IsAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}

internal sealed record GateOptions(
    string SourceAssembly,
    string WorkerPath,
    string? PhysicalAssemblyTemplate,
    bool ReuseExisting)
{
    public static GateOptions Parse(IReadOnlyList<string> args)
    {
        string? source = null;
        string? worker = null;
        string? template = null;
        var reuseExisting = false;
        for (var index = 0; index < args.Count;)
        {
            if (string.Equals(args[index], "--reuse-existing", StringComparison.OrdinalIgnoreCase))
            {
                reuseExisting = true;
                index++;
                continue;
            }
            if (index + 1 >= args.Count)
                throw new ArgumentException($"参数缺少值：{args[index]}");
            switch (args[index].ToLowerInvariant())
            {
                case "--se-asm": source = args[index + 1]; break;
                case "--worker": worker = args[index + 1]; break;
                case "--assembly-template": template = args[index + 1]; break;
                default: throw new ArgumentException($"未知参数：{args[index]}");
            }
            index += 2;
        }
        if (!reuseExisting && string.IsNullOrWhiteSpace(template))
            throw new ArgumentException("普通门禁缺少 --assembly-template；安全重试门禁请传入 --reuse-existing。");
        return new GateOptions(
            Path.GetFullPath(source ?? throw new ArgumentException("缺少 --se-asm")),
            Path.GetFullPath(worker ?? throw new ArgumentException("缺少 --worker")),
            string.IsNullOrWhiteSpace(template) ? null : Path.GetFullPath(template),
            reuseExisting);
    }
}

internal sealed class GateResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string SourceAssembly { get; set; } = string.Empty;
    public string WorkerPath { get; set; } = string.Empty;
    public string PhysicalAssemblyTemplate { get; set; } = string.Empty;
    public bool ExistingPartOutputsUnchanged { get; set; }
    public string? OriginalAssemblyTemplate { get; set; }
    public string? GateDirectory { get; set; }
    public int OccurrenceCount { get; set; }
    public int UniquePartCount { get; set; }
    public int SuppressedCount { get; set; }
    public int UnresolvedCount { get; set; }
    public int WorkerExitCode { get; set; }
    public int ComponentExpected { get; set; }
    public int ComponentActual { get; set; }
    public int ComponentFixed { get; set; }
    public double MaxRotationDeviation { get; set; }
    public double MaxTranslationDeviationMeters { get; set; }
    public bool SourceHashesUnchanged { get; set; }
    public bool TemplateRestored { get; set; }
    public List<WorkerEvent> WorkerEvents { get; } = [];
    public List<OutputFact> OutputFiles { get; set; } = [];
    public int[] EdgeProcessesAfter { get; set; } = [];
    public int[] SolidWorksProcessesAfter { get; set; } = [];
}

internal sealed record WorkerRun(int ExitCode, IReadOnlyList<WorkerEvent> Events, string StandardError);
internal sealed record ExpectedComponent(string Path, double[] Transform);
internal sealed record ActualComponent(string Path, double[] Transform, bool IsFixed);
internal sealed record OutputFact(string Path, long Length, string Sha256);
