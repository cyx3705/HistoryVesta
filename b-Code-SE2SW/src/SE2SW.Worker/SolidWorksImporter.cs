using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class SolidWorksImporter
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int DocumentTypePart = 1;
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;

    public static int Import(
        BatchRequest request,
        IReadOnlyList<ConversionJob> exported,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        if (exported.Count == 0)
            return 0;

        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        FeatureRecognizer? recognizer = null;
        bool? originalCommandInProgress = null;
        var failed = 0;

        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.ComNotRegistered,
                    "未检测到 SolidWorks COM 注册。");
            try
            {
                applicationObject = Activator.CreateInstance(applicationType)
                    ?? throw new InvalidOperationException("COM 返回了空实例。");
            }
            catch (Exception ex)
            {
                throw new ClassifiedConversionException(
                    ComErrorClassifier.Classify(ex, ConversionErrorClass.AppLaunchFailed),
                    "SolidWorks COM 实例创建失败：" + ex.Message,
                    ex);
            }
            dynamic application = applicationObject;
            try
            {
                interop = SolidWorksInteropBridge.Create(applicationObject, applicationType);
            }
            catch (Exception ex)
            {
                ownership.Resolve(0);
                throw new ClassifiedConversionException(
                    ConversionErrorClass.AppLaunchFailed,
                    "SolidWorks 官方 Interop 初始化失败：" + ex.Message,
                    ex);
            }
            long windowHandle = 0;
            try
            {
                windowHandle = interop.GetWindowHandle();
            }
            catch
            {
                // 唯一新增 PID 仍可作为所有权后备证据。
            }
            ownership.Resolve(windowHandle);

            // SolidWorks 是单实例：用户已经开着 SolidWorks 时，CreateInstance 一定附着到那个会话，
            // 不会新建进程。此时继续转换但绝不 ExitApp、绝不改可见性，用户的未保存工作不受影响。
            if (!ownership.OwnsInstance)
            {
                reporter.Report(
                    null,
                    ConversionStage.SolidWorksImport,
                    "正在使用你已打开的 SolidWorks 会话，本次转换不会关闭它。",
                    errorClass: ConversionErrorClass.CadProcessOwnershipUnknown);
            }
            else
            {
                originalCommandInProgress = TryGetBoolean(() => application.CommandInProgress);
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
                TryRun(() => application.CommandInProgress = true);
            }

            recognizer = FeatureRecognizer.Prepare(interop);
            if (request.RecognizeFeatures)
            {
                if (!recognizer.IsAvailable && !request.ContinueWhenRecognitionFails)
                {
                    throw new ClassifiedConversionException(
                        recognizer.UnavailableReason,
                        recognizer.StatusMessage);
                }

                reporter.Report(
                    null,
                    ConversionStage.FeatureRecognition,
                    recognizer.StatusMessage,
                    errorClass: recognizer.IsAvailable
                        ? ConversionErrorClass.None
                        : recognizer.UnavailableReason);
            }

            for (var jobIndex = 0; jobIndex < exported.Count; jobIndex++)
            {
                var job = exported[jobIndex];
                cancellationToken.ThrowIfCancellationRequested();
                reporter.Report(job.Id, ConversionStage.SolidWorksImport, "正在生成 SolidWorks 零件。");
                object? importData = null;
                object? modelObject = null;
                object? extensionObject = null;
                string? temporaryPath = null;
                FeatureOutcome? featureOutcome = null;
                string? firstRecognitionDiagnostic = null;
                try
                {
                    temporaryPath = TemporaryOutput.For(job.SolidWorksPath);
                    var importAttempt = 0;
                    while (true)
                    {
                        try
                        {
                            importData = interop.GetImportFileData(job.XtPath);
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Parasolid 在部分 SW 版本中没有专用导入选项对象；LoadFile4 接受 null。
                            importData = null;
                        }
                        var loadErrors = 0;
                        try
                        {
                            modelObject = interop.LoadFile4(job.XtPath, "r", importData, out loadErrors);
                        }
                        catch (Exception ex)
                        {
                            throw ImportOperationFailed("LoadFile4", ex);
                        }
                        if (modelObject is null)
                            throw new InvalidDataException($"SolidWorks 未返回零件文档，错误码 {loadErrors}。");

                        int documentType;
                        try
                        {
                            documentType = interop.GetDocumentType(modelObject);
                        }
                        catch (Exception ex)
                        {
                            throw ImportOperationFailed("ModelDoc2.GetType", ex);
                        }
                        if (documentType != DocumentTypePart)
                            throw new InvalidDataException("SolidWorks 导入结果不是零件文档。");

                        // ---- V2.0：特征识别 + 草图完全定义（在保存之前做完） ----
                        // 未启用识别时不产生 FeatureOutcome，行为与 V1.x 完全一致。
                        if (request.RecognizeFeatures)
                            featureOutcome = recognizer!.Process(
                                modelObject,
                                interop.GetTitle(modelObject),
                                request,
                                (stage, message) => reporter.Report(job.Id, stage, message),
                                cancellationToken);

                        if (!ShouldRetryFirstRecognition(
                                jobIndex,
                                importAttempt,
                                recognizer!.IsAvailable,
                                featureOutcome))
                        {
                            if (firstRecognitionDiagnostic is not null && featureOutcome is not null)
                            {
                                var retryDiagnostic = string.IsNullOrWhiteSpace(featureOutcome.Diagnostic)
                                    ? "重试成功"
                                    : "重试诊断=" + featureOutcome.Diagnostic;
                                featureOutcome = featureOutcome with
                                {
                                    Diagnostic = $"首件重新导入恢复；首次诊断={firstRecognitionDiagnostic}；{retryDiagnostic}",
                                };
                            }
                            break;
                        }

                        firstRecognitionDiagnostic = featureOutcome?.Diagnostic ?? "识别返回 0";
                        reporter.Report(
                            job.Id,
                            ConversionStage.FeatureRecognition,
                            "首件识别尚未就绪，正在关闭未保存文档并重新导入一次。");
                        TryClose(interop, modelObject);
                        ComRelease.Final(modelObject);
                        modelObject = null;
                        ComRelease.Final(importData);
                        importData = null;
                        importAttempt++;
                        StaMessagePump.PumpAndWait(750, cancellationToken);
                    }

                    if (featureOutcome is { DegradedToDumbSolid: true }
                        && !request.ContinueWhenRecognitionFails)
                    {
                        throw new ClassifiedConversionException(
                            featureOutcome.RecognizedFeatureCount == 0
                                ? ConversionErrorClass.FeatureRecognitionEmpty
                                : ConversionErrorClass.FeatureCreationFailed,
                            "特征识别未产生结果，且当前设置不允许降级为哑实体。");
                    }

                    try
                    {
                        extensionObject = interop.GetExtension(modelObject);
                    }
                    catch (Exception ex)
                    {
                        throw ImportOperationFailed("ModelDoc2.Extension", ex);
                    }
                    int saveErrors;
                    int saveWarnings;
                    bool saved;
                    try
                    {
                        saved = interop.SaveAs3(
                            extensionObject,
                            temporaryPath,
                            SaveAsCurrentVersion,
                            SaveAsSilent,
                            out saveErrors,
                            out saveWarnings);
                    }
                    catch (Exception ex)
                    {
                        throw ImportOperationFailed("ModelDocExtension.SaveAs3", ex);
                    }
                    if (!saved || saveErrors != 0)
                        throw new IOException($"SolidWorks 保存失败，错误码 {saveErrors}，警告码 {saveWarnings}。");

                    var title = interop.GetTitle(modelObject);
                    if (!string.IsNullOrWhiteSpace(title))
                        interop.CloseDocument(title);
                    ComRelease.Final(extensionObject);
                    extensionObject = null;
                    ComRelease.Final(modelObject);
                    modelObject = null;

                    var output = FileProbe.WaitForStableNonEmptyFile(temporaryPath, cancellationToken);
                    TemporaryOutput.Commit(temporaryPath, job.SolidWorksPath);
                    temporaryPath = null;
                    reporter.Report(
                        job.Id,
                        ConversionStage.Completed,
                        $"转换完成，SolidWorks 零件 {output.Length} 字节。{DescribeFeatures(featureOutcome)}",
                        nativeError: saveErrors,
                        nativeWarning: saveWarnings,
                        errorClass: featureOutcome is { SketchTotal: > 0 } outcome
                            && outcome.SketchFullyDefined < outcome.SketchTotal
                                ? ConversionErrorClass.SketchNotFullyDefined
                                : ConversionErrorClass.None,
                        feature: featureOutcome);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    TryClose(interop, modelObject);
                    throw new OperationCanceledException("SolidWorks 导入已取消。", ex, cancellationToken);
                }
                catch (Exception ex)
                {
                    failed++;
                    TryClose(interop, modelObject);
                    reporter.Report(
                        job.Id,
                        ConversionStage.Failed,
                        "SolidWorks 导入失败：" + ex.Message,
                        true,
                        ex.HResult,
                        errorClass: ClassifyImportError(ex));
                }
                finally
                {
                    TemporaryOutput.DeleteIfExists(temporaryPath);
                    ComRelease.Final(extensionObject);
                    ComRelease.Final(modelObject);
                    ComRelease.Final(importData);
                }
            }

            return failed;
        }
        finally
        {
            // 先还原 FeatureWorks 相关的用户设置，再退出应用。
            recognizer?.Dispose();
            if (applicationObject is not null)
            {
                dynamic application = applicationObject;
                if (originalCommandInProgress is bool commandInProgress)
                    TryRun(() => application.CommandInProgress = commandInProgress);
                if (ownership.OwnsInstance)
                    TryRun(() =>
                    {
                        if (interop is not null)
                            interop.ExitApplication();
                        else
                            application.ExitApp();
                    });
            }
            interop?.Dispose();
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
                _ = ownership.WaitForOwnedExit(TimeSpan.FromSeconds(30));
        }
    }

    private static string DescribeFeatures(FeatureOutcome? outcome)
    {
        if (outcome is null)
            return string.Empty;
        if (outcome.DegradedToDumbSolid)
        {
            var reason = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? "" : $"（{outcome.Diagnostic}）";
            return outcome.RecognizedFeatureCount == 0
                ? $" 未识别到特征，输出为导入实体。{reason}"
                : $" 识别 {outcome.RecognizedFeatureCount} 个特征但未能生成，输出为导入实体。{reason}";
        }
        var diagnostic = string.IsNullOrWhiteSpace(outcome.Diagnostic)
            ? string.Empty
            : $"（{outcome.Diagnostic}）";
        return $" 识别 {outcome.RecognizedFeatureCount} 个特征，草图完全定义 {outcome.SketchFullyDefined}/{outcome.SketchTotal}。{diagnostic}";
    }

    internal static bool ShouldRetryFirstRecognition(
        int jobIndex,
        int importAttempt,
        bool featureWorksAvailable,
        FeatureOutcome? outcome)
        => jobIndex == 0
           && importAttempt == 0
           && featureWorksAvailable
           && outcome is
           {
               RecognizedFeatureCount: 0,
               DegradedToDumbSolid: true,
           };

    private static ConversionErrorClass ClassifyImportError(Exception exception)
    {
        if (exception is FileNotFoundException missing)
            return string.Equals(Path.GetExtension(missing.FileName), ".SLDPRT", StringComparison.OrdinalIgnoreCase)
                ? ConversionErrorClass.OutputEmpty
                : ConversionErrorClass.InputMissing;
        if (exception is TimeoutException)
            return ConversionErrorClass.OutputUnstable;
        if (exception is IOException io && io.Message.Contains("保存失败", StringComparison.Ordinal))
            return ConversionErrorClass.SaveFailed;
        return ComErrorClassifier.Classify(exception, ConversionErrorClass.ImportFailed);
    }

    private static ClassifiedConversionException ImportOperationFailed(string operation, Exception exception)
        => new(
            ComErrorClassifier.Classify(exception, ConversionErrorClass.ImportFailed),
            $"{operation} 调用失败：{exception.Message}",
            exception);

    private static void TryClose(SolidWorksInteropBridge? interop, object? modelObject)
    {
        if (interop is null || modelObject is null)
            return;
        TryRun(() =>
        {
            var title = interop.GetTitle(modelObject);
            if (!string.IsNullOrWhiteSpace(title))
                interop.CloseDocument(title);
        });
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

    private static bool? TryGetBoolean(Func<object> valueFactory)
    {
        try
        {
            return Convert.ToBoolean(valueFactory());
        }
        catch
        {
            return null;
        }
    }
}
