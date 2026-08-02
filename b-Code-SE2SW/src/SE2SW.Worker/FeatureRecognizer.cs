using System.Diagnostics;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V2.0：对导入后的哑实体执行 FeatureWorks 自动特征识别，再对识别出的每个草图执行完全定义。
/// 设计依据与实测数据见 docs/05-V2.0-特征识别与草图完全定义.md。
/// </summary>
internal sealed class FeatureRecognizer : IDisposable
{
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";

    // swUserPreferenceToggle_e
    private const int SwUseEnglishLanguageFeatureNames = 262;

    // swLoadAddinError_e
    private const int SwLoadAddinSuccess = 0;
    private const int SwLoadAddinAlreadyLoaded = 2;
    private const int SwLoadAddinLicenseError = 7;

    // fwAdvancedOptions_e
    private const short FwAdvAddConstraintsToSketch = 1;
    private const short FwAdvAllowWizardHoleRecognition = 4;

    // fwFeatureCreationOptions_e：不加 fwAllowFailFeatureCreation，
    // 否则会生成带重建错误的特征，宁可整体降级为哑实体。
    private const short FwAddConstraintsToSketch = 1;

    // fwAutomaticRecognitionOptions_e 的全部 10 位。
    // 实测：只传实体类选项（1|4|8|16|32 = 61）恒返回 0，必须传满 0x3FF。
    private const int AutomaticRecognitionAllOptions = 0x3FF;

    // swSketchFullyDefineRelationType_e 全部关系类型
    private const int AllSketchRelations = 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 | 512;

    // swConstrainedStatus_e
    private const int SwFullyConstrained = 3;

    private const int SeedFaceAttempts = 5;
    private const int SeedFaceRetryDelayMilliseconds = 400;
    private const int DocumentActivationAttempts = 5;
    private const int DocumentActivationDelayMilliseconds = 250;
    private const int RecognitionAttempts = 10;
    private const int RecognitionRetryDelayMilliseconds = 300;

    private const string SketchFeatureTypeName = "ProfileFeature";
    private const int MarkHorizontalDatum = 2;
    private const int MarkVerticalDatum = 4;

    private readonly SolidWorksInteropBridge _interop;
    private readonly bool? _originalEnglishFeatureNames;
    private object? _featureWorks;

    private FeatureRecognizer(
        SolidWorksInteropBridge interop,
        object? featureWorks,
        bool? originalEnglishFeatureNames,
        ConversionErrorClass unavailableReason,
        string statusMessage)
    {
        _interop = interop;
        _featureWorks = featureWorks;
        _originalEnglishFeatureNames = originalEnglishFeatureNames;
        UnavailableReason = unavailableReason;
        StatusMessage = statusMessage;
    }

    public bool IsAvailable => _featureWorks is not null;

    public ConversionErrorClass UnavailableReason { get; }

    public string StatusMessage { get; }

    /// <summary>
    /// 批次开始时准备一次：加载 FeatureWorks 加载项，并把特征名切到英文。
    /// 不抛异常——不可用时返回一个 IsAvailable=false 的实例，由调用方决定降级还是失败。
    /// </summary>
    public static FeatureRecognizer Prepare(SolidWorksInteropBridge interop)
    {
        bool? originalEnglishFeatureNames = null;
        try
        {
            // "Point1@Origin" 是英文名。中文界面下不切换会选不中基准点。
            // 必须在打开任何文档之前设置：实测该开关不追溯已打开的文档。
            originalEnglishFeatureNames = interop.GetUserPreferenceToggle(SwUseEnglishLanguageFeatureNames);
            interop.SetUserPreferenceToggle(SwUseEnglishLanguageFeatureNames, true);
        }
        catch
        {
            originalEnglishFeatureNames = null;
        }

        object? featureWorks;
        try
        {
            featureWorks = interop.GetAddInObject(FeatureWorksProgId);
        }
        catch
        {
            featureWorks = null;
        }

        if (featureWorks is not null)
            return new FeatureRecognizer(
                interop, featureWorks, originalEnglishFeatureNames,
                ConversionErrorClass.None, "FeatureWorks 已就绪。");

        // FeatureWorks 随产品安装但默认不启用，此时 GetAddInObject 返回 null 且不抛异常。
        var path = interop.ResolveFeatureWorksPath();
        if (path is null || !File.Exists(path))
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "未找到 FeatureWorks 加载项，本批次不做特征识别。");
        }

        int loadResult;
        try
        {
            loadResult = interop.LoadAddIn(path);
        }
        catch (Exception ex)
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "加载 FeatureWorks 失败：" + ex.Message);
        }

        if (loadResult == SwLoadAddinLicenseError)
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "当前 SolidWorks 授权不含 FeatureWorks，本批次不做特征识别。");
        }

        if (loadResult is not (SwLoadAddinSuccess or SwLoadAddinAlreadyLoaded))
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                $"加载 FeatureWorks 返回 {loadResult}，本批次不做特征识别。");
        }

        try
        {
            featureWorks = interop.GetAddInObject(FeatureWorksProgId);
        }
        catch
        {
            featureWorks = null;
        }

        return featureWorks is not null
            ? new FeatureRecognizer(
                interop, featureWorks, originalEnglishFeatureNames,
                ConversionErrorClass.None, "FeatureWorks 已按需加载。")
            : new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "FeatureWorks 加载后仍无法获取自动化对象，本批次不做特征识别。");
    }

    /// <summary>对一个已导入的零件文档执行识别与草图完全定义。不抛异常，结果全部落在返回值里。</summary>
    public FeatureOutcome Process(
        object model,
        string documentTitle,
        BatchRequest request,
        Action<ConversionStage, string> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var statuses = new List<string>();

        if (!IsAvailable || !request.RecognizeFeatures)
        {
            return Degraded(0, false, stopwatch, statuses, "未启用特征识别或 FeatureWorks 不可用。");
        }

        var recognized = 0;
        var created = false;
        var step = "开始";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(ConversionStage.FeatureRecognition, "正在识别特征。");

            // FeatureWorks 只作用于活动文档。附着用户会话时必须确认导入零件真的
            // 成为 ActiveDoc，不能把识别命令误发到用户原有装配体。
            step = "ActivateDoc3";
            var activationFailure = ActivateExpectedDocument(documentTitle, cancellationToken);
            if (activationFailure is not null)
            {
                return Degraded(0, false, stopwatch, statuses, activationFailure);
            }

            // GetAddInObject 在没有活动零件时取得的对象可能仍处于加载态。文档激活后
            // 重新获取一次，使首件与后续零件走完全相同的 FeatureWorks 会话状态。
            step = "GetAddInObject";
            var refreshFailure = RefreshFeatureWorksForActiveDocument();
            if (refreshFailure is not null)
            {
                return Degraded(0, false, stopwatch, statuses, refreshFailure);
            }

            step = "SetFeatureWorksOptions";
            TryRun(() =>
            {
                _ = _interop.SetAdvancedOptions(
                    _featureWorks!,
                    FwAdvAddConstraintsToSketch | FwAdvAllowWizardHoleRecognition);
            });
            TryRun(() => _ = _interop.SetPerformanceOptions(_featureWorks!, 0));

            step = "RecognizeFeatureAutomatic";
            SeedFaceSelection? seedSelection = null;
            RecognitionAttemptResult recognition;
            try
            {
                recognition = RunRecognitionAttempts(
                    () =>
                    {
                        seedSelection?.Dispose();
                        seedSelection = SelectSeedFaceWithRetry(model, cancellationToken);
                        return seedSelection.Failure;
                    },
                    () =>
                    {
                        try
                        {
                            return _interop.RecognizeFeatureAutomatic(
                                _featureWorks!,
                                AutomaticRecognitionAllOptions);
                        }
                        finally
                        {
                            // FeatureWorks consumes the current face selection during this call.
                            // Keep the face/body RCWs alive until it returns, then release them.
                            seedSelection?.Dispose();
                            seedSelection = null;
                        }
                    },
                    () => StaMessagePump.PumpAndWait(RecognitionRetryDelayMilliseconds, cancellationToken),
                    cancellationToken);
            }
            finally
            {
                seedSelection?.Dispose();
            }
            if (recognition.SeedFailure is not null)
            {
                return Degraded(0, false, stopwatch, statuses,
                    $"无法预选种子面：{recognition.SeedFailure}；标题={documentTitle}；" +
                    $"活动文档={_interop.GetActiveDocumentTitle()}");
            }

            recognized = recognition.RecognizedFeatureCount;
            if (recognized <= 0)
            {
                return Degraded(
                    0,
                    false,
                    stopwatch,
                    statuses,
                    $"RecognizeFeatureAutomatic 连续 {recognition.Attempts} 次返回 0。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            step = "CreateFeatures";
            created = _interop.CreateFeatures(_featureWorks!, FwAddConstraintsToSketch);
            if (!created)
            {
                return Degraded(recognized, false, stopwatch, statuses, "CreateFeatures 返回 false。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Degraded(recognized, created, stopwatch, statuses, $"{step} 失败：{ex.Message}");
        }

        var total = 0;
        var fullyDefined = 0;
        if (request.FullyDefineSketches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(ConversionStage.SketchFullyDefine, "正在完全定义草图。");
            (total, fullyDefined) = FullyDefineSketches(model, statuses, cancellationToken);
        }

        return new FeatureOutcome(
            recognized,
            created,
            total,
            fullyDefined,
            statuses,
            false,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// 种子面必须选中，否则 RecognizeFeatureAutomatic 会静默返回 0。
    /// 实测刚导入完就选会偶发返回 false（视图尚未就绪），因此做有界重试。
    /// </summary>
    private SeedFaceSelection SelectSeedFaceWithRetry(object model, CancellationToken cancellationToken)
    {
        var failure = "未知选择失败";
        for (var attempt = 0; attempt < SeedFaceAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
                StaMessagePump.PumpAndWait(SeedFaceRetryDelayMilliseconds, cancellationToken);

            _interop.ClearSelection(model);
            var selection = _interop.SelectSeedFace(model);
            if (selection.Failure is null)
                return selection;
            failure = selection.Failure;
            selection.Dispose();
        }

        return SeedFaceSelection.Failed(failure);
    }

    private string? ActivateExpectedDocument(string documentTitle, CancellationToken cancellationToken)
    {
        string? lastNote = null;
        for (var attempt = 1; attempt <= DocumentActivationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var errors = _interop.ActivateDocument(documentTitle);
                StaMessagePump.PumpAndWait(DocumentActivationDelayMilliseconds, cancellationToken);
                var activeTitle = _interop.GetActiveDocumentTitle();
                if (DocumentTitlesMatch(documentTitle, activeTitle))
                    return null;
                lastNote = $"attempt={attempt}, errors={errors}, active={activeTitle}";
            }
            catch (Exception ex)
            {
                lastNote = $"attempt={attempt}, exception={ex.Message}";
            }
        }

        return $"无法激活导入零件；标题={documentTitle}；{lastNote}";
    }

    internal static bool DocumentTitlesMatch(string expected, string active)
        => string.Equals(expected?.Trim(), active?.Trim(), StringComparison.OrdinalIgnoreCase);

    private string? RefreshFeatureWorksForActiveDocument()
    {
        object? refreshed;
        try
        {
            refreshed = _interop.GetAddInObject(FeatureWorksProgId);
        }
        catch (Exception ex)
        {
            return "活动文档就绪后重新获取 FeatureWorks 失败：" + ex.Message;
        }

        if (refreshed is null)
            return "活动文档就绪后 FeatureWorks 自动化对象为空。";
        if (ReferenceEquals(refreshed, _featureWorks))
        {
            // GetAddInObject returned another COM reference to the same RCW.
            ComRelease.One(refreshed);
        }
        else
        {
            ComRelease.One(_featureWorks);
            _featureWorks = refreshed;
        }

        return null;
    }

    /// <summary>
    /// FeatureWorks 首次识别会异步初始化，同一文档前几次可能静默返回 0。
    /// 每次调用前必须重新选择种子面，并在失败后泵送 STA 消息再等待。
    /// </summary>
    internal static RecognitionAttemptResult RunRecognitionAttempts(
        Func<string?> selectSeedFace,
        Func<int> recognize,
        Action waitBetweenAttempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RecognitionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seedFailure = selectSeedFace();
            if (seedFailure is not null)
                return new RecognitionAttemptResult(0, attempt - 1, seedFailure);

            var recognized = recognize();
            if (recognized > 0)
                return new RecognitionAttemptResult(recognized, attempt, null);

            if (attempt < RecognitionAttempts)
                waitBetweenAttempts();
        }

        return new RecognitionAttemptResult(0, RecognitionAttempts, null);
    }

    internal readonly record struct RecognitionAttemptResult(
        int RecognizedFeatureCount,
        int Attempts,
        string? SeedFailure);

    private static FeatureOutcome Degraded(
        int recognized,
        bool created,
        Stopwatch stopwatch,
        List<string> statuses,
        string diagnostic)
        => new(recognized, created, 0, 0, statuses, true, stopwatch.ElapsedMilliseconds, diagnostic);

    private (int Total, int FullyDefined) FullyDefineSketches(
        object model,
        List<string> statuses,
        CancellationToken cancellationToken)
    {
        var sketchFeatures = CollectSketchFeatures(model);
        var extension = _interop.GetExtension(model);
        var sketchManager = _interop.GetSketchManager(model);
        var fullyDefined = 0;

        try
        {
            foreach (var feature in sketchFeatures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = SafeFeatureName(feature);
                var inSketch = false;
                try
                {
                    _interop.ClearSelection(model);
                    _interop.SelectFeature(feature);
                    _interop.InsertSketch(sketchManager, false);
                    inSketch = true;

                    // HorizontalDatumDisp / VerticalDatumDisp 传 null 时，
                    // SolidWorks 用"选择标记 = 6"的实体作基准，这里预选原点。
                    _interop.SelectByID2(
                        extension, "Point1@Origin", "EXTSKETCHPOINT",
                        MarkHorizontalDatum | MarkVerticalDatum);

                    _interop.FullyDefineSketch(sketchManager, AllSketchRelations);
                    _interop.InsertSketch(sketchManager, true);
                    inSketch = false;

                    var status = ReadConstrainedStatus(feature);
                    statuses.Add($"{name}={DescribeStatus(status)}");
                    if (status == SwFullyConstrained)
                        fullyDefined++;
                }
                catch (OperationCanceledException)
                {
                    if (inSketch)
                        TryRun(() => _interop.InsertSketch(sketchManager, true));
                    throw;
                }
                catch (Exception ex)
                {
                    if (inSketch)
                        TryRun(() => _interop.InsertSketch(sketchManager, true));
                    statuses.Add($"{name}=失败({ex.Message})");
                }
            }
        }
        finally
        {
            foreach (var feature in sketchFeatures)
                ComRelease.Final(feature);
            ComRelease.Final(sketchManager);
            ComRelease.Final(extension);
        }

        return (sketchFeatures.Count, fullyDefined);
    }

    /// <summary>
    /// 只收集 ProfileFeature。原点是 OriginProfileFeature，不会被命中，正是期望行为。
    /// </summary>
    private List<object> CollectSketchFeatures(object model)
    {
        var result = new List<object>();
        var feature = _interop.FirstFeature(model);
        while (feature is not null)
        {
            object? next = null;
            try
            {
                next = _interop.NextFeature(feature);
                if (string.Equals(_interop.FeatureTypeName(feature), SketchFeatureTypeName, StringComparison.Ordinal))
                {
                    result.Add(feature);
                    feature = next;
                    continue;
                }
            }
            catch
            {
                next = null;
            }

            ComRelease.Final(feature);
            feature = next;
        }

        return result;
    }

    private int ReadConstrainedStatus(object feature)
    {
        object? sketch = null;
        try
        {
            sketch = _interop.SpecificFeature(feature);
            return sketch is null ? 0 : _interop.GetConstrainedStatus(sketch);
        }
        catch
        {
            return 0;
        }
        finally
        {
            ComRelease.Final(sketch);
        }
    }

    private string SafeFeatureName(object feature)
    {
        try
        {
            return _interop.FeatureName(feature);
        }
        catch
        {
            return "(未知草图)";
        }
    }

    private static string DescribeStatus(int status) => status switch
    {
        1 => "未知",
        2 => "欠定义",
        3 => "完全定义",
        4 => "过定义",
        5 => "无解",
        6 => "无效解",
        7 => "自动求解关闭",
        _ => "读取失败",
    };

    public void Dispose()
    {
        if (_originalEnglishFeatureNames is bool original)
            TryRun(() => _interop.SetUserPreferenceToggle(SwUseEnglishLanguageFeatureNames, original));
        ComRelease.Final(_featureWorks);
        _featureWorks = null;
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

}
