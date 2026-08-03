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
    public static bool CanReuse(AssemblyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!File.Exists(node.OutputPath))
            return false;

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
