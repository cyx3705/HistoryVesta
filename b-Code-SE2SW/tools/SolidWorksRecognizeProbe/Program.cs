using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolidWorks.Interop.fworks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksRecognizeProbe;

/// <summary>
/// V2.0 可实现性探针：.x_t -> 导入 -> FeatureWorks 特征识别 -> 每个草图完全定义 -> .SLDPRT。
/// 只回答"能不能做、代价多大"，不是生产实现。
/// </summary>
internal static class Program
{
    private const string ProgId = "SldWorks.Application";
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";
    private const string SolidWorksProcessName = "SLDWORKS";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? input = null;
        string? output = null;
        bool visible = false;
        bool skipRecognize = false;
        string? prepareFrom = null;
        string? inspect = null;
        int? exitOwnedPid = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input": input = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--visible": visible = true; break;
                case "--skip-recognize": skipRecognize = true; break;
                case "--prepare-xt-from": prepareFrom = args[++i]; break;
                case "--inspect": inspect = args[++i]; break;
                case "--exit-owned": exitOwnedPid = int.Parse(args[++i]); break;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    return 2;
            }
        }

        if (exitOwnedPid is int expectedPid)
        {
            return ExitOwnedApplication(expectedPid);
        }

        if (inspect is not null)
        {
            return Inspect(inspect);
        }

        if (input is null || output is null)
        {
            Console.Error.WriteLine("用法: --input <abs .x_t> --output <abs .SLDPRT> [--visible] [--skip-recognize]");
            return 2;
        }

        var result = new ProbeResult { Input = input, Output = output };
        var sw = Stopwatch.StartNew();
        ISldWorks? app = null;
        ModelDoc2? model = null;
        int[] before = GetPids();

        try
        {
            if (File.Exists(output))
            {
                throw new InvalidOperationException($"输出已存在，拒绝覆盖：{output}");
            }

            Type type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new InvalidOperationException($"ProgID 未注册：{ProgId}");
            result.Clsid = type.GUID.ToString("B").ToUpperInvariant();

            app = (ISldWorks)Activator.CreateInstance(type)!;
            result.TimingsMs["AppLaunch"] = Mark(sw);

            int[] after = GetPids();
            result.NewPids = after.Except(before).ToArray();
            result.CreatedNewInstance = result.NewPids.Length > 0;
            result.PreexistingPids = before;

            app.Visible = visible;
            result.SolidWorksRevision = app.RevisionNumber();

            // 一键完全定义示例里用的是 "Point1@Origin" 这种英文名。中文界面下要么改中文名，
            // 要么打开这个开关强制英文特征名。探针直接打开，并在结束时还原。
            bool oldEnglishNames = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseEnglishLanguageFeatureNames);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseEnglishLanguageFeatureNames, true);
            result.OldEnglishFeatureNames = oldEnglishNames;

            bool old3Di = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect);
            result.Old3DInterconnect = old3Di;

            // ---- 阶段 0：先把 FeatureWorks 加载项挂起来 ----
            // 顺序有讲究：加载项在 Load 时才挂接文档事件，先导入再加载会看不到已打开的文档。
            object? fwObject = null;
            if (!skipRecognize)
            {
                fwObject = app.GetAddInObject(FeatureWorksProgId);
                if (fwObject is null)
                {
                    string? dll = ResolveFeatureWorksDll();
                    result.FeatureWorksDll = dll;
                    if (dll is not null && File.Exists(dll))
                    {
                        result.LoadAddInResult = app.LoadAddIn(dll);
                        fwObject = app.GetAddInObject(FeatureWorksProgId);
                        result.LoadedAddInOnDemand = fwObject is not null;
                    }
                }

                result.FeatureWorksAddInObtained = fwObject is not null;
                result.FeatureWorksObjectType = fwObject?.GetType().FullName;
                result.TimingsMs["LoadAddIn"] = Mark(sw);
            }

            // ---- 对照组：把一个原生 SLDPRT 先导出成 .x_t，用来区分
            //      "API 没接通" 和 "这个零件本来就识别不出特征"。
            if (prepareFrom is not null)
            {
                int openErrors = 0;
                int openWarnings = 0;
                var source = (ModelDoc2?)app.OpenDoc6(
                    prepareFrom,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref openErrors,
                    ref openWarnings);

                if (source is null)
                {
                    throw new InvalidOperationException($"OpenDoc6 失败：{prepareFrom} errors={openErrors}");
                }

                int xtErrors = 0;
                int xtWarnings = 0;
                bool exported = source.Extension.SaveAs3(
                    input,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, null, ref xtErrors, ref xtWarnings);
                result.Notes.Add($"对照组导出 {Path.GetFileName(prepareFrom)} -> .x_t：{exported}，errors={xtErrors}");
                app.CloseDoc(source.GetTitle());
                Marshal.FinalReleaseComObject(source);
                result.TimingsMs["PrepareXt"] = Mark(sw);
            }

            // ---- 阶段 1：导入 .x_t ----
            int errors = 0;
            object? importData = app.GetImportFileData(input);
            result.ImportDataType = importData?.GetType().FullName ?? "(null)";
            model = app.LoadFile4(input, "r", importData, ref errors);
            result.LoadFileErrors = errors;
            result.TimingsMs["Import"] = Mark(sw);

            if (model is null)
            {
                throw new InvalidOperationException($"LoadFile4 返回 null，errors={errors}");
            }

            // FeatureWorks 作用于 ActiveDoc。附着到用户已有会话时，导入的文档不一定是活动文档。
            int activateErrors = 0;
            app.ActivateDoc3(model.GetTitle(), false, 0, ref activateErrors);
            result.ActivateDocErrors = activateErrors;
            result.ActiveDocTitle = (app.ActiveDoc as ModelDoc2)?.GetTitle();

            result.DocTypeAfterImport = ((swDocumentTypes_e)model.GetType()).ToString();
            result.BodyCountAfterImport = CountBodies(model);
            result.FeatureNamesAfterImport = ListFeatures(model);

            // ---- 阶段 2：FeatureWorks 特征识别 ----
            if (!skipRecognize)
            {
                if (fwObject is null)
                {
                    result.Notes.Add($"GetAddInObject(\"{FeatureWorksProgId}\") 返回 null —— 加载项未启用。");
                }
                else
                {
                    var fw = (IFeatureWorksApp)fwObject;

                    // 建议：让 FeatureWorks 在生成特征时就给草图加几何关系，
                    // 后续 FullyDefineSketch 的工作量会小很多。
                    result.SetAdvancedOptions = fw.SetAdvancedOptions(
                        (short)(fwAdvancedOptions_e.fwAdvAddConstraintsToSketch
                              | fwAdvancedOptions_e.fwAdvAllowWizardHoleRecognition));

                    // 实测结论：只勾选实体类选项（1|4|8|16|32 = 61）恒返回 0；
                    // 把 fwAutomaticRecognitionOptions_e 的 10 个位全部置上（0x3FF）才会真正识别。
                    int recognizeOptions = 0x3FF;

                    result.SetPerformanceOptions = fw.SetPerformanceOptions(0);

                    // 官方 VBA 示例在识别前先选了一个面。这里选实体的第一个面，
                    // 用来区分"必须有预选"和"API 本身没生效"。
                    model.ClearSelection2(true);
                    result.FaceSelected = SelectFirstFace(model);

                    // 实测：同一个调用连续发多次，前几次返回 0，之后才返回真实数量。
                    // 说明 FeatureWorks 不是同步完成的，必须重试并给 SW 泵消息的机会。
                    for (int attempt = 1; attempt <= 10; attempt++)
                    {
                        model.ClearSelection2(true);
                        result.FaceSelected = SelectFirstFace(model);
                        int count = fw.RecognizeFeatureAutomatic(recognizeOptions);
                        result.RecognitionTrials[$"attempt{attempt:00}"] = count;
                        if (count > 0)
                        {
                            result.RecognizedFeatureCount = count;
                            result.RecognizeAttempts = attempt;
                            break;
                        }

                        PumpAndWait(300);
                    }

                    if (result.RecognizedFeatureCount < 0)
                    {
                        result.RecognizedFeatureCount = 0;
                    }

                    result.TimingsMs["Recognize"] = Mark(sw);

                    result.CreateFeaturesResult = fw.CreateFeatures(
                        (short)fwFeatureCreationOptions_e.fwAddConstraintsToSketch);
                    result.TimingsMs["CreateFeatures"] = Mark(sw);

                    Marshal.FinalReleaseComObject(fwObject);
                }
            }

            result.FeatureNamesAfterRecognize = ListFeatures(model);

            // ---- 阶段 3：逐个草图完全定义 ----
            result.Sketches = FullyDefineAllSketches(model);
            result.TimingsMs["FullyDefine"] = Mark(sw);

            // ---- 阶段 4：保存 .SLDPRT ----
            int saveErrors = 0;
            int saveWarnings = 0;
            result.SaveResult = model.Extension.SaveAs3(
                output,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                null,
                ref saveErrors,
                ref saveWarnings);
            result.SaveErrors = saveErrors;
            result.SaveWarnings = saveWarnings;
            result.TimingsMs["Save"] = Mark(sw);

            if (File.Exists(output))
            {
                result.OutputLength = new FileInfo(output).Length;
            }

            app.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swUseEnglishLanguageFeatureNames, oldEnglishNames);

            result.Success = result.SaveResult && result.OutputLength > 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        finally
        {
            try
            {
                if (model is not null)
                {
                    app?.CloseDoc(model.GetTitle());
                    Marshal.FinalReleaseComObject(model);
                }
            }
            catch { }

            try
            {
                if (app is not null && result.CreatedNewInstance)
                {
                    app.ExitApp();
                    result.ExitAppCalled = true;
                }
            }
            catch { }

            if (app is not null)
            {
                try { Marshal.FinalReleaseComObject(app); } catch { }
            }

            if (result.ExitAppCalled)
            {
                result.LeakedPids = result.NewPids.Where(IsAlive).ToArray();
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));

        return result.Success ? 0 : 1;
    }

    /// <summary>验收清理用：仅当系统中唯一的 SolidWorks PID 与预期一致时正常退出。</summary>
    private static int ExitOwnedApplication(int expectedPid)
    {
        var pids = GetPids();
        if (pids.Length != 1 || pids[0] != expectedPid)
        {
            Console.Error.WriteLine(
                $"拒绝退出 SolidWorks：预期唯一 PID={expectedPid}，实际={string.Join(',', pids)}");
            return 1;
        }

        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (type is null)
        {
            Console.Error.WriteLine("SldWorks.Application 未注册。");
            return 2;
        }

        ISldWorks? app = null;
        try
        {
            app = (ISldWorks)Activator.CreateInstance(type)!;
            app.ExitApp();
            return 0;
        }
        finally
        {
            if (app is not null && Marshal.IsComObject(app))
            {
                Marshal.FinalReleaseComObject(app);
            }
        }
    }

    /// <summary>
    /// 遍历特征树，对每个 ProfileFeature（草图）执行"完全定义草图"。
    /// 模式来自 SW API Help 示例 Fully_Define_Underdefined_Sketch_Example_CSharp。
    /// </summary>
    private static List<SketchFact> FullyDefineAllSketches(ModelDoc2 model)
    {
        var facts = new List<SketchFact>();
        var extension = model.Extension;
        var sketchManager = model.SketchManager;

        const int markHorizontal = 2;
        const int markVertical = 4;
        int relations =
            (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Horizontal
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Vertical
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Coincident
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Concentric
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Parallel
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Perpendicular
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Tangent
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Equal;

        var sketchFeatures = new List<Feature>();
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null)
        {
            if (feature.GetTypeName2() == "ProfileFeature")
            {
                sketchFeatures.Add(feature);
            }

            feature = (Feature?)feature.GetNextFeature();
        }

        foreach (Feature sketchFeature in sketchFeatures)
        {
            var fact = new SketchFact { Name = sketchFeature.Name };
            try
            {
                var sketch = sketchFeature.GetSpecificFeature2() as Sketch;
                fact.SegmentCount = ((object[]?)sketch?.GetSketchSegments())?.Length ?? -1;
                fact.StatusBefore = ((swConstrainedStatus_e)(sketch?.GetConstrainedStatus() ?? 0)).ToString();

                model.ClearSelection2(true);
                sketchFeature.Select2(false, 0);
                sketchManager.InsertSketch(false);   // 进入编辑

                // 参数 HorizontalDatumDisp/VerticalDatumDisp 传 null 时，
                // SW 要求用选择标记 6（= 2|4）预选基准，这里用原点。
                fact.DatumSelected = extension.SelectByID2(
                    "Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, false,
                    markHorizontal | markVertical, null, 0);

                fact.FullyDefineReturn = sketchManager.FullyDefineSketch(
                    true, true, relations, true,
                    1, null, 1, null, 1, 1);

                sketchManager.InsertSketch(true);    // 退出并重建

                var after = sketchFeature.GetSpecificFeature2() as Sketch;
                fact.StatusAfter = ((swConstrainedStatus_e)(after?.GetConstrainedStatus() ?? 0)).ToString();
                fact.Ok = fact.StatusAfter == swConstrainedStatus_e.swFullyConstrained.ToString();
            }
            catch (Exception ex)
            {
                fact.Error = ex.Message;
                try { sketchManager.InsertSketch(true); } catch { }
            }

            facts.Add(fact);
        }

        return facts;
    }

    private static List<string> ListFeatures(ModelDoc2 model)
    {
        var names = new List<string>();
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null && names.Count < 200)
        {
            names.Add($"{feature.Name} [{feature.GetTypeName2()}]");
            feature = (Feature?)feature.GetNextFeature();
        }

        return names;
    }

    /// <summary>验收用：重新打开一个 .SLDPRT，打印特征树与每个草图的约束状态。</summary>
    private static int Inspect(string path)
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (type is null)
        {
            Console.Error.WriteLine("SldWorks.Application 未注册。");
            return 2;
        }

        var app = (ISldWorks)Activator.CreateInstance(type)!;
        int errors = 0;
        int warnings = 0;
        var model = (ModelDoc2?)app.OpenDoc6(
            path,
            (int)swDocumentTypes_e.swDocPART,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            string.Empty,
            ref errors,
            ref warnings);

        if (model is null)
        {
            Console.Error.WriteLine($"OpenDoc6 失败 errors={errors} warnings={warnings}");
            return 1;
        }

        Console.WriteLine($"文件: {path}");
        Console.WriteLine($"打开: errors={errors} warnings={warnings}");
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null)
        {
            var typeName = feature.GetTypeName2();
            var line = $"  {feature.Name} [{typeName}]";
            if (typeName == "ProfileFeature" && feature.GetSpecificFeature2() is Sketch sketch)
            {
                line += $"  约束状态={(swConstrainedStatus_e)sketch.GetConstrainedStatus()}";
            }

            Console.WriteLine(line);
            feature = (Feature?)feature.GetNextFeature();
        }

        app.CloseDoc(model.GetTitle());
        return 0;
    }

    /// <summary>抽干本 STA 线程的消息队列并等待，给 SolidWorks 的异步工作留出时间。</summary>
    private static void PumpAndWait(int milliseconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            while (PeekMessage(out NativeMessage msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(20);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint flags);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    private static bool SelectFirstFace(ModelDoc2 model)
    {
        try
        {
            var part = (PartDoc)model;
            var bodies = (object[]?)part.GetBodies2((int)swBodyType_e.swSolidBody, false);
            if (bodies is null || bodies.Length == 0)
            {
                return false;
            }

            var body = (Body2)bodies[0];
            var face = body.GetFirstFace() as Face2;
            if (face is null)
            {
                return false;
            }

            var entity = (Entity)face;
            return entity.Select4(false, null);
        }
        catch
        {
            return false;
        }
    }

    private static int CountBodies(ModelDoc2 model)
    {
        try
        {
            var part = (PartDoc)model;
            var bodies = (object[]?)part.GetBodies2((int)swBodyType_e.swSolidBody, false);
            return bodies?.Length ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// FeatureWorks CLSID {7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}，
    /// InprocServer32 是相对路径 ".\fworks\fworks.dll"，需要拼上 SolidWorks 安装目录。
    /// 安装目录从 SldWorks.Application 的 LocalServer32 反推，不硬编码。
    /// </summary>
    private static string? ResolveFeatureWorksDll()
    {
        using Microsoft.Win32.RegistryKey? fwKey = Microsoft.Win32.Registry.ClassesRoot
            .OpenSubKey(@"CLSID\{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}\InprocServer32");
        if (fwKey?.GetValue(null) is not string relative)
        {
            return null;
        }

        if (Path.IsPathRooted(relative))
        {
            return relative;
        }

        Type? appType = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (appType is null)
        {
            return null;
        }

        using Microsoft.Win32.RegistryKey? swKey = Microsoft.Win32.Registry.ClassesRoot
            .OpenSubKey($@"CLSID\{appType.GUID:B}\LocalServer32");
        if (swKey?.GetValue(null) is not string server)
        {
            return null;
        }

        string exe = server.Trim('"').Split('"')[0];
        string? dir = Path.GetDirectoryName(exe);
        return dir is null ? null : Path.GetFullPath(Path.Combine(dir, relative));
    }

    private static int[] GetPids() =>
        Process.GetProcessesByName(SolidWorksProcessName).Select(p => { int id = p.Id; p.Dispose(); return id; }).OrderBy(x => x).ToArray();

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static long Mark(Stopwatch sw)
    {
        long ms = sw.ElapsedMilliseconds;
        sw.Restart();
        return ms;
    }
}

internal sealed class ProbeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string? Clsid { get; set; }
    public string? SolidWorksRevision { get; set; }
    public int[] PreexistingPids { get; set; } = Array.Empty<int>();
    public int[] NewPids { get; set; } = Array.Empty<int>();
    public bool CreatedNewInstance { get; set; }
    public bool ExitAppCalled { get; set; }
    public int[] LeakedPids { get; set; } = Array.Empty<int>();
    public bool OldEnglishFeatureNames { get; set; }
    public bool Old3DInterconnect { get; set; }

    public string? ImportDataType { get; set; }
    public int LoadFileErrors { get; set; }
    public int ActivateDocErrors { get; set; }
    public string? ActiveDocTitle { get; set; }
    public string? DocTypeAfterImport { get; set; }
    public int BodyCountAfterImport { get; set; }
    public List<string> FeatureNamesAfterImport { get; set; } = new();

    public string? FeatureWorksDll { get; set; }
    public int LoadAddInResult { get; set; } = int.MinValue;
    public bool LoadedAddInOnDemand { get; set; }
    public bool FeatureWorksAddInObtained { get; set; }
    public string? FeatureWorksObjectType { get; set; }
    public bool SetAdvancedOptions { get; set; }
    public bool SetPerformanceOptions { get; set; }
    public bool FaceSelected { get; set; }
    public int RecognizedFeatureCount { get; set; } = -1;
    public int RecognizedFeatureCountLateBound { get; set; } = -1;
    public Dictionary<string, int> RecognitionTrials { get; set; } = new();
    public int RecognizeAttempts { get; set; }
    public bool CreateFeaturesResult { get; set; }
    public List<string> FeatureNamesAfterRecognize { get; set; } = new();

    public List<SketchFact> Sketches { get; set; } = new();

    public bool SaveResult { get; set; }
    public int SaveErrors { get; set; }
    public int SaveWarnings { get; set; }
    public long OutputLength { get; set; }

    public Dictionary<string, long> TimingsMs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

internal sealed class SketchFact
{
    public string? Name { get; set; }
    public int SegmentCount { get; set; }
    public string? StatusBefore { get; set; }
    public bool DatumSelected { get; set; }
    public int FullyDefineReturn { get; set; }
    public string? StatusAfter { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
