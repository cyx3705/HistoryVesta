using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V3.3 §3.7：装配产物的复用判定。
///
/// 零件的规则是"产物比源文件新"。这条对装配**不成立**：子装配 <c>.asm</c> 本身没改，
/// 但它里面的某个 <c>.par</c> 改了，产物就已经过期。因此装配产物必须比它**递归依赖的每一个文件**都新。
///
/// 任一依赖读不到修改时间即判为不可复用——宁可重转一次，不出静默旧几何。
/// 与 <see cref="AssemblyPartReusePlanner"/> 同口径：从不覆盖用户文件，不安全就报错让用户自己移走。
/// </summary>
internal static class AssemblyNodeReusePlanner
{
    /// <returns><c>true</c> 表示可以直接复用已有产物，跳过生成。</returns>
    public static bool CanReuse(AssemblyNode node, bool requireMateRebuild = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!File.Exists(node.OutputPath))
            return false;

        // 时间戳只能证明几何依赖没有更新，不能证明这个 SLDASM 曾按当前关系集建立过
        // 配合。若在此处复用，RebuildMates=true 会得到一个“成功”的旧装配，但根本没有
        // 调用 SolidWorksMateRebuilder。安全策略是不覆盖未知归属的现有文件，并明确要求
        // 用户移走旧产物后重新生成；绝不能静默跳过关系重建。
        if (requireMateRebuild)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.OutputExists,
                $"已有装配产物无法证明已按当前关系重建，拒绝静默复用：{node.OutputPath}；"
                    + "请将本次转换生成的旧 SLDASM 移出 SW 文件夹后重新转换。");
        }

        var output = new FileInfo(node.OutputPath);
        output.Refresh();
        if (output.Length == 0)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.OutputEmpty,
                $"已有装配产物为空，拒绝覆盖或复用：{node.OutputPath}");
        }

        foreach (var dependency in Enumerable.Repeat(node.SourceAssemblyPath, 1).Concat(node.Dependencies))
        {
            var source = new FileInfo(dependency);
            if (!source.Exists)
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.SubAssemblyReuseStale,
                    $"依赖文件不存在，无法证明已有产物是新的：{dependency}；"
                        + $"请移走 {node.OutputPath} 后重试。");
            }

            if (output.LastWriteTimeUtc < source.LastWriteTimeUtc)
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.SubAssemblyReuseStale,
                    $"已有装配产物早于它的依赖 {Path.GetFileName(dependency)}，拒绝复用："
                        + $"{node.OutputPath}；请移走旧产物后重试。");
            }
        }

        return true;
    }
}
