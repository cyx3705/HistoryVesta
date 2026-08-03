using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class AssemblyConverter
{
    public static int Convert(
        AssemblyBatchRequest request,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var partRequest = new BatchRequest(
            request.BatchId,
            request.Mode,
            request.PartJobs,
            request.Overwrite,
            request.RecognizeFeatures,
            request.FullyDefineSketches,
            request.FeatureRecognitionTimeoutSeconds,
            ContinueWhenRecognitionFails: true);

        var reusePlan = AssemblyPartReusePlanner.Create(request.PartJobs, cancellationToken);
        foreach (var job in reusePlan.ReusableSolidWorksParts)
        {
            reporter.Report(
                job.Id,
                ConversionStage.Skipped,
                $"复用已有 SLDPRT，不再重复转换：{job.SolidWorksPath}",
                artifact: ConversionArtifactKind.SolidWorksPart,
                reuseKind: ReuseKind.ExistingSolidWorksPart);
        }
        foreach (var job in reusePlan.ImportFromExistingXt)
        {
            reporter.Report(
                job.Id,
                ConversionStage.Skipped,
                $"复用已有 XT，跳过 Solid Edge 导出：{job.XtPath}",
                artifact: ConversionArtifactKind.Xt,
                reuseKind: ReuseKind.ExistingXt);
        }

        var exported = reusePlan.NeedsExport.Count == 0
            ? Array.Empty<ConversionJob>()
            : SolidEdgeExporter.Export(
                partRequest with { Jobs = reusePlan.NeedsExport },
                reporter,
                cancellationToken);
        var importJobs = reusePlan.ImportFromExistingXt.Concat(exported).ToArray();
        _ = SolidWorksImporter.Import(partRequest, importJobs, reporter, cancellationToken);
        var successfulJobs = request.PartJobs
            .Where(job => File.Exists(job.SolidWorksPath) && new FileInfo(job.SolidWorksPath).Length > 0)
            .ToDictionary(job => Path.GetFullPath(job.SourcePath), StringComparer.OrdinalIgnoreCase);
        var partFailed = request.PartJobs.Count - successfulJobs.Count;
        if (successfulJobs.Count == 0)
        {
            reporter.Report(
                null,
                ConversionStage.Failed,
                "所有零件均转换失败，不生成空装配体。",
                true,
                errorClass: ConversionErrorClass.PartStageFailed);
            return 1;
        }
        if (partFailed > 0 && !request.ContinueWhenPartFails)
        {
            reporter.Report(
                null,
                ConversionStage.Failed,
                $"{partFailed} 个零件转换失败，按默认策略不生成缺件装配体。",
                true,
                errorClass: ConversionErrorClass.PartStageFailed);
            return 1;
        }

        var failedPartPaths = request.PartJobs
            .Where(job => !successfulJobs.ContainsKey(Path.GetFullPath(job.SourcePath)))
            .Select(job => job.SourcePath)
            .ToArray();

        // V3.3：拿到拓扑序的装配节点就走嵌套；拿不到（旧协议）退回 V3.0 的展平。
        var outcome = request.Nodes is { Count: > 0 } nodes
            ? SolidWorksNestedAssemblyBuilder.Build(
                nodes,
                successfulJobs,
                request.PartJobs.Count - partFailed,
                partFailed,
                request.Occurrences.Count(item => item.IsSuppressed),
                reporter,
                cancellationToken,
                failedPartPaths)
            : SolidWorksAssemblyBuilder.Build(
                request.AssemblyOutputPath,
                request.Occurrences
                    .Where(item => item.IsSubAssembly || item.IsSuppressed
                        || successfulJobs.ContainsKey(Path.GetFullPath(item.SourcePath)))
                    .ToArray(),
                successfulJobs,
                request.Overwrite,
                request.PartJobs.Count - partFailed,
                partFailed,
                reporter,
                cancellationToken,
                failedPartPaths);

        var completionMessage = outcome.SubAssemblyTotal > 0
            ? $"装配转换完成：{outcome.SubAssemblyBuilt + 1} 个装配文件、最大 {outcome.MaxDepth} 层，"
                + $"插入 {outcome.ComponentInserted}/{outcome.ComponentTotal}，固定 {outcome.ComponentFixed}。"
            : $"装配转换完成：插入 {outcome.ComponentInserted}/{outcome.ComponentTotal}，固定 {outcome.ComponentFixed}。";
        if (outcome.ReusedAssemblyCount > 0)
        {
            completionMessage += $" 本次复用 {outcome.ReusedAssemblyCount} 个已有装配；"
                + $"其中按计划含 {outcome.ReusedAssemblyPlannedComponentCount} 个直接组件，未在本次运行中重新打开核验。";
        }

        reporter.Report(
            null,
            ConversionStage.Completed,
            completionMessage,
            assembly: outcome,
            artifact: ConversionArtifactKind.SolidWorksAssembly);
        return partFailed == 0 ? 0 : 1;
    }
}
