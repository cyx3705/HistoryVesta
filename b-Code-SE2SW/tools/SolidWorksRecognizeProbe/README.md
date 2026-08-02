# SolidWorksRecognizeProbe

SE2SW V2.0 可实现性探针：`.x_t` -> 导入 -> FeatureWorks 自动特征识别 -> 每个草图完全定义 -> `.SLDPRT`。

只回答"能不能做、怎么做、代价多大"，不是生产实现。

## 构建

```bash
dotnet build SolidWorksRecognizeProbe.csproj -c Release
```

与 Solid Edge 探针不同，这里 **`dotnet build` 可以直接用**：Dassault 在
`api\redist` 下正式提供可再分发的托管 Interop 程序集，用普通 `<Reference>` 即可，
不需要 `<COMReference>`，也就不受 MSB4803 限制。

安装目录通过 `SwApiRedist` 属性可覆盖：

```bash
dotnet build -c Release -p:SwApiRedist="D:\SW\api\redist"
```

## 运行

```bash
SolidWorksRecognizeProbe.exe --input "D:\out\part.x_t" --output "D:\out\part.SLDPRT" --visible
```

| 参数 | 说明 |
|---|---|
| `--input <路径>` | 绝对路径的 `.x_t` |
| `--output <路径>` | 绝对路径的 `.SLDPRT`；已存在则拒绝 |
| `--visible` | 显示 SolidWorks 窗口（排障用） |
| `--skip-recognize` | 跳过特征识别，只做 V1.0 的导入+保存，用于对照 |
| `--prepare-xt-from <sldprt>` | 先把一个原生零件导出成 `--input` 指定的 `.x_t`，用于构造对照样件 |
| `--inspect <sldprt>` | 独立重开 SLDPRT，打印特征树与草图约束状态 |
| `--exit-owned <pid>` | 验收清理：仅当系统中唯一 SW PID 与参数一致时调用官方 `ExitApp` |

输出为结构化 JSON，包含识别数量、每个草图的完全定义前后约束状态、各阶段耗时。

`--exit-owned` 不按进程名强杀。PID 数量或值不完全匹配时命令拒绝执行，用于避免误关用户已经打开的 SolidWorks 会话。

## 实测得到的两个硬性前提

1. **必须预选一个种子面**。不预选时 `RecognizeFeatureAutomatic` 恒返回 0，不报错。
2. **识别选项必须传满 `0x3FF`**（`fwAutomaticRecognitionOptions_e` 全部 10 位）。
   只传实体类选项（`fwExtrudeOption|fwRevolve|fwHoles|fwChamfils|fwRibs` = 61）同样恒返回 0。

两条都不满足时表现完全一样：返回 0、无异常、特征树不变。详见
[../../docs/05-V2.0-特征识别与草图完全定义.md](../../docs/05-V2.0-特征识别与草图完全定义.md)。
