namespace SE2SW.Contracts;

public enum ConversionMode
{
    Ohs,
    External,
}

public enum ConversionStage
{
    Queued,
    SolidEdgeExport,
    SolidWorksImport,
    FeatureRecognition,
    SketchFullyDefine,
    Completed,
    Skipped,
    Failed,
    Cancelled,
    AssemblyProbe,
    AssemblyBuild,
}

public enum ConversionErrorClass
{
    None,
    ComNotRegistered,
    AppLaunchFailed,
    LicenseUnavailable,
    InputMissing,
    InputInvalid,
    InputLocked,
    OutputExists,
    OutputNotWritable,
    OutputEmpty,
    OutputUnstable,
    OutputFormatInvalid,
    CallRejected,
    Timeout,
    Cancelled,
    ExportFailed,
    ImportFailed,
    SaveFailed,
    CadProcessOwnershipUnknown,
    FeatureWorksUnavailable,
    FeatureRecognitionEmpty,
    FeatureCreationFailed,
    SketchNotFullyDefined,
    Unknown,
    AssemblyOpenFailed,
    OccurrenceUnresolved,
    DuplicateOutputName,
    AssemblyTemplateMissing,
    ComponentInsertFailed,
    ComponentTransformFailed,
    AssemblySaveFailed,
    PartStageFailed,
    // V3.3 装配嵌套。枚举按数值序列化，新值只能追加在末尾。
    SubAssemblyBuildFailed,
    SubAssemblyCycleDetected,
    SubAssemblyReuseStale,
}

public sealed record ConversionJob(
    string Id,
    string SourcePath,
    string XtPath,
    string SolidWorksPath);

public sealed record BatchRequest(
    string BatchId,
    ConversionMode Mode,
    IReadOnlyList<ConversionJob> Jobs,
    bool Overwrite = false,
    // V2.0：FeatureWorks 自动特征识别。不可用时按 ContinueWhenRecognitionFails 决定是否降级。
    bool RecognizeFeatures = true,
    // V2.0：对识别出的每个草图执行"完全定义草图"。依赖 RecognizeFeatures。
    bool FullyDefineSketches = true,
    int FeatureRecognitionTimeoutSeconds = 120,
    bool ContinueWhenRecognitionFails = true);

/// <summary>V2.0 单个零件的特征识别与草图定义结果，随 Completed 事件回传。</summary>
public sealed record FeatureOutcome(
    int RecognizedFeatureCount,
    bool FeaturesCreated,
    int SketchTotal,
    int SketchFullyDefined,
    IReadOnlyList<string> SketchStatuses,
    bool DegradedToDumbSolid,
    long ElapsedMilliseconds,
    string? Diagnostic = null);

public sealed record WorkerEvent(
    string BatchId,
    string? JobId,
    ConversionStage Stage,
    string Message,
    bool IsError = false,
    int? HResult = null,
    int? NativeError = null,
    int? NativeWarning = null,
    ConversionErrorClass ErrorClass = ConversionErrorClass.None,
    FeatureOutcome? Feature = null,
    AssemblyOutcome? Assembly = null,
    ConversionArtifactKind? Artifact = null,
    ReuseKind? ReuseKind = null);

/// <summary>装配树中的一个实例。Solid Edge GetMatrix 已返回顶层世界矩阵。</summary>
public sealed record AssemblyOccurrence(
    string OccurrenceId,
    string? ParentId,
    string SourcePath,
    bool IsSubAssembly,
    bool IsSuppressed,
    bool IsHidden,
    double[] WorldTransform,
    string? Diagnostic);

/// <summary>
/// V3.3：某个装配文档里的一个直接子项。
///
/// <paramref name="LocalTransform"/> 的元素布局与 <see cref="AssemblyOccurrence.WorldTransform"/>
/// 完全一致（16 元素行主序，旋转 0,1,2/4,5,6/8,9,10，平移 12..14，米），
/// 差别只在参考系：它相对**本装配自己的原点**，不是顶层世界系。
/// 顶层节点的局部矩阵恰好等于世界矩阵，因为顶层的参考系就是世界系。
/// </summary>
public sealed record AssemblyChild(
    string Name,
    string SourcePath,
    bool IsSubAssembly,
    bool IsHidden,
    double[] LocalTransform,
    string? Diagnostic = null);

/// <summary>
/// V3.3：一个装配文档的一级读数。每个唯一 <c>.asm</c> 一条，含顶层。
///
/// 由 Worker 把该 <c>.asm</c> 作为**独立顶层文档**打开后读出——这样读到的矩阵天然就是
/// 该文档坐标系下的局部矩阵，不需要用父级世界矩阵求逆换算。
/// </summary>
public sealed record AssemblyDocumentReading(
    string SourceAssemblyPath,
    IReadOnlyList<AssemblyChild> Children,
    IReadOnlyList<string> Warnings);

/// <summary>
/// V3.3：一个待生成的 <c>.SLDASM</c> 及其直接子项。
/// 列表由 <c>AssemblyGraphBuilder</c> 按拓扑序产出：被依赖者在前，顶层在最后。
/// </summary>
public sealed record AssemblyNode(
    string SourceAssemblyPath,
    string OutputPath,
    bool IsRoot,
    int Depth,
    IReadOnlyList<AssemblyChild> Children,
    IReadOnlyList<string> Dependencies);

public sealed record AssemblyProbeRequest(
    string BatchId,
    string SourceAssemblyPath,
    string ResultPath);

public sealed record AssemblyProbeResult(
    string SourceAssemblyPath,
    IReadOnlyList<AssemblyOccurrence> Occurrences,
    IReadOnlyList<string> UniquePartPaths,
    int SuppressedCount,
    int UnresolvedCount,
    int OrderedPartCount,
    int SynchronousPartCount,
    IReadOnlyList<string> Warnings,
    // V3.3：逐文档的一级读数。为 null 表示 V3.0 格式的旧结果（只能展平）。
    IReadOnlyList<AssemblyDocumentReading>? Documents = null);

public sealed record AssemblyBatchRequest(
    string BatchId,
    ConversionMode Mode,
    string SourceAssemblyPath,
    string AssemblyOutputPath,
    IReadOnlyList<ConversionJob> PartJobs,
    IReadOnlyList<AssemblyOccurrence> Occurrences,
    bool Overwrite = false,
    bool RecognizeFeatures = false,
    bool FullyDefineSketches = false,
    bool ContinueWhenPartFails = false,
    int FeatureRecognitionTimeoutSeconds = 120,
    // V3.3：拓扑序的装配节点。为 null 时退化为 V3.0 的展平行为。
    IReadOnlyList<AssemblyNode>? Nodes = null);

/// <summary>
/// 装配转换结果。
///
/// V3.3 起 <paramref name="ComponentTotal"/> / <paramref name="ComponentInserted"/> 的语义变了：
/// 从"全部叶零件"变为"**全部装配节点的直接子项之和**"。嵌套装配里同一个叶零件只被它的直接父级计一次。
/// </summary>
public sealed record AssemblyOutcome(
    int ComponentTotal,
    int ComponentInserted,
    int ComponentFixed,
    int PartConverted,
    int PartFailed,
    int SkippedSuppressed,
    double MaxOriginDeviationMeters,
    long ElapsedMilliseconds,
    string? Diagnostic,
    // V3.3 装配嵌套。
    int SubAssemblyTotal = 0,
    int SubAssemblyBuilt = 0,
    int MaxDepth = 1);
