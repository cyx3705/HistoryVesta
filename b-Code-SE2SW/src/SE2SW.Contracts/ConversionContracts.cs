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
    AssemblyOutcome? Assembly = null);

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
    IReadOnlyList<string> Warnings);

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
    int FeatureRecognitionTimeoutSeconds = 120);

public sealed record AssemblyOutcome(
    int ComponentTotal,
    int ComponentInserted,
    int ComponentFixed,
    int PartConverted,
    int PartFailed,
    int SkippedSuppressed,
    double MaxOriginDeviationMeters,
    long ElapsedMilliseconds,
    string? Diagnostic);
