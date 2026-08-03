# SE2SW 格式转换模块

> 状态：3.3.1 已正式部署；V3.3 子装配嵌套已完成并通过真机门禁（3 个装配文件、3 层、22 个叶组件最大平移偏差 5.6e-17 m）。
> 目标宿主：OneHistoryStudio / AppShell 模块宿主  
> 目标格式：Solid Edge `.par/.asm` -> Parasolid `.x_t` -> SolidWorks `.SLDPRT/.SLDASM`

SE2SW 是 019 Studio 工具箱中的 OHS UI 模块。它提供 OHS 兼容模式和外界模式，使用
Solid Edge COM 导出 Parasolid，再使用 SolidWorks COM 导入并保存为 SolidWorks 零件。V3.1.0 在普通工具窗口
`se2sw` 内提供“零件转换 / 装配转换 / OHS 兼容”三个同级模式：零件转换对应原外界零件模式，装配转换保持外界
装配流程，OHS 兼容当前只承载零件流程。V3.3 起装配**按源装配的层级生成嵌套 `.SLDASM`**：每个唯一 `.par`
各转换一次，每个唯一子装配生成自己的 `.SLDASM`，父级把它当一个组件插入；全部组件固定，不翻译配合。

嵌套所需的局部矩阵不靠世界矩阵求逆换算，而是把每个子装配 `.asm` 作为独立顶层文档打开后直接读取——
读到的本来就是该文档坐标系下的值。转换前用世界矩阵与局部矩阵互相印证（阈值 1e-9），
参考系用错会在触碰 CAD 之前被拦下。

装配转换会优先使用 SolidWorks 当前默认装配模板；默认值为空、是虚拟 token、不是物理文件或无法创建文档时，
Worker 会从本机 SolidWorks 官方模板目录选择物理 `.asmdot`，不永久修改用户设置。失败后再次转换时，已有且有效的
`SLDPRT` 会直接复用，只有 XT 时跳过 Solid Edge 导出；空文件、错误 XT 或早于源 `.par` 的旧产物会被拒绝。

外界模式统一输出到源目录下的 `XT\` 与 `SW\`。扫描和装配探查不会创建目录；只有用户点击转换且环境检查
通过后才创建。旧版平铺在源目录的 `.x_t/.SLDPRT` 仍会被识别为已有产物，不会重复转换或自动迁移。

V1.1 固定使用 `PartDocument.SaveBody(..., 2, 0, Missing, Missing)` 生成文本 Parasolid，输出经
稳定性、`FORMAT=text` 和源文件 SHA-256 校验后才提交。SolidWorks 端从 COM 注册的安装目录加载
官方 Interop，通过强类型接口调用 `LoadFile4` / `SaveAs3`；厂商程序集不复制进模块目录。

本目录采用 019 项目的 `b-Code-*` 源码命名规则。注册元数据单独放在项目根目录的 `z-SE2SW` 中，
业务源码和设计文档仍留在本目录。

SE2SW 的需求、实现、COM 研究、诊断、转换实测和生命周期验收文档必须留在本目录的 `docs`；OHS 项目
只记录版本、哈希、注册和装载等宿主集成事实，不得把 SE2SW 文档或原始验证附件上抛到 `b-Office`。

## 工程

```text
src/SE2SW.Contracts   批处理请求与进度协议
src/SE2SW             OHS WPF UI 模块、扫描和目录预检
src/SE2SW.Worker      独立 x64 STA COM 工作进程
tests/SE2SW.Smoke     不启动 CAD 的目录与扫描冒烟测试
tests/SE2SW.UiSmoke   仅显示窗口的渲染冒烟壳
tools/AssemblyProbe    SE/SW occurrence、矩阵、模板、插入与固定探针
tools/RelationProbe    SE 装配关系与装配级 Parasolid 导出只读探针
tools/AssemblyProductionGate  生产 Worker 真机装配收口门禁
```

```powershell
dotnet build .\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
```

真实 CAD 验收必须先把样件复制到临时目录。Worker 保留零件动词 `--request`，并新增
`--probe-assembly` 与 `--assembly`；三者都使用 `<请求 JSON> --cancel <信号文件>`。请求协议中的 `mode`
按数值枚举序列化：OHS 为 `0`，外界模式为 `1`。

OHS 注册清单位于项目根目录 `z-SE2SW/module.manifest.json`，MCP 档位为 `hidden`，转换只能从本机 UI
明确触发。

## 文档

- [当前转换合同](docs/00-当前转换合同.md) — 当前模式、输出、复用、协议、版本与发布边界的唯一人工说明
- [需求与技术设计](docs/01-需求与技术设计.md)
- [COM 预研与验证计划](docs/02-COM预研与验证计划.md)
- [给 Opus 5 的 Solid Edge COM 问题单](docs/03-给Opus5的SolidEdge-COM问题单.md)
- [Opus 5 答复：Solid Edge 导出验证报告](docs/04-Opus5答复-SolidEdge导出验证报告.md) —— 含
  [tools/SolidEdgeExportProbe](tools/SolidEdgeExportProbe) 探针与实测矩阵
- [V2.0：特征识别与草图完全定义](docs/05-V2.0-特征识别与草图完全定义.md) —— 已实现并实测通过；
  含 [tools/SolidWorksRecognizeProbe](tools/SolidWorksRecognizeProbe) 探针、六条必须遵守的约束与实现记录
- [V2.2.0：OHS 内嵌工作页设计与实施验收](docs/06-V2.2.0-OHS内嵌页面设计.md) —— 保留首次
  内嵌的历史设计；当前窗口 ID `se2sw` 已迁移到 AppShell 0.7.2 普通工具窗口
- [V2.2.0：生命周期部署与外界模式实测](docs/07-V2.2.0-生命周期部署与外界模式实测.md) —— 活动转换
  生命周期、正式槽装载/热重载与外界模式真实 CAD 转换摘要
- [V2.3.0：首件特征识别修复](docs/08-V2.3.0-首件特征识别修复.md) —— 活动文档验真、FeatureWorks
  会话刷新、种子面 COM 生命周期与首件重新导入恢复
- [V2.3.1：文件夹扫描与模式识别修复](docs/09-V2.3.1-文件夹扫描与模式识别修复.md) —— 支持直接选择
  `b-Module-SE`，普通零件目录可从默认 OHS 模式自动切换到外界模式
- [V3.0：装配体转换与输出目录分层](docs/10-V3.0-装配体转换与输出目录分层.md) —— 设计合同与边界
- [V3.0：工程实施记录](docs/11-V3.0-工程实施记录.md) —— 生产代码、窗口、协议与门禁实现
- [V3.0：真实 CAD 验收](docs/12-V3.0-真实CAD验收.md) —— L0–L4、双烧杯回归和实测矩阵
- [V3.0：发布与部署收口](docs/13-V3.0-发布与部署收口.md) —— 正式槽备份、同步、哈希与回退结论
- [V3.0.1：零件/装配双页入口修复](docs/14-V3.0.1-零件与装配双页入口修复.md) —— 单一普通工具窗口、
  双页选择、默认零件页与正式部署记录
- [V3.0.2：装配模板与安全重试修复](docs/15-V3.0.2-装配模板与安全重试修复.md) —— 虚拟模板物理回退、
  XT/SLDPRT 分级复用、真实“风滚子”重试验收与部署收口
- [V3.1.0：三模式界面美化升级](docs/16-V3.1.0-三模式界面美化升级.md) —— 单层模式导航、页面减负、
  桌面与紧凑窗口渲染验收
- [V3.1.1：装配页空闲状态栏精简](docs/17-V3.1.1-装配页空闲状态栏精简.md) —— 空闲时移除冗余页脚，
  运行时保留进度与取消能力
- [V3.2：装配关系探针](docs/18-V3.2-装配关系探针.md) —— 设计合同；只读关系探针与装配级导出探查，
  不改任何转换行为；含 [tools/RelationProbe](tools/RelationProbe)
- [V3.2：装配关系实测报告](docs/19-V3.2-装配关系实测报告.md) —— `Relations3d` 类型与参数实测、
  `GetGeometryN` 世界系点/向量、`SaveCopyAs` 装配级 Parasolid 导出与 SW 导入形态
- [V3.3：子装配嵌套链路](docs/20-V3.3-子装配嵌套链路.md) —— 设计合同与实施记录；每个子装配递归生成
  `.SLDASM`、局部矩阵采集与自校验、装配树标注、三层真机门禁
- [V1.1 工程实施与验收](docs/05-V1.1-工程实施与验收.md)

- [V3.3.1：质量更新设计](docs/21-V3.3.1-质量更新设计.md) — 版本、路径、协议与进度语义的单一真值治理
- [V3.3.1：实施与发布收口](docs/22-V3.3.1-实施与发布收口.md) — 门禁、正式槽备份、运行时认证和全量哈希核验

## V3.0 范围

- 零件窗口支持批量 `.par`；装配窗口支持单个 `.asm`，V3.0 装配只开放外界模式。
- 中间格式固定使用文本 Parasolid `.x_t`；界面可简称为“XT”。
- 输出支持 `.SLDPRT`，并把 `.asm` 输出为全部组件固定的 `.SLDASM`（V3.3 起按源层级嵌套，见下）。
- 不处理钣金 `.psm`、工程图 `.dft`；装配引用中的非 `.par/.asm` 文件跳过并报告。
- 同一批次串行转换，避免两个 CAD 应用的 COM 自动化并发冲突。
- 不翻译 Mates、材料、属性或配置；隐藏件仍插入并报告。

## V3.3 范围

- 每个唯一子装配生成自己的 `.SLDASM`，父级把它当一个组件插入，层级与源装配同构；层数不限。
- 每层只按本层的直接子项和局部矩阵生成，与它被谁引用无关；因此同一子装配被多处引用、
  各实例姿态不同，都由各自父级的插入矩阵承担，不需要额外处理。
- 跨层复用的零件与子装配默认只生成一份产物；**本版不为消除副本增加任何机制**，
  真出现副本按事实报告，不视为缺陷。
- 装配引用成环会被检出并阻断（`SubAssemblyCycleDetected`），不靠递归深度兜底。
- 已有装配产物不再是硬阻断：转换时核验它是否比**递归依赖的每一个文件**都新，
  安全的直接复用，过期或为空报错让用户自行移走，从不覆盖用户文件。
- Solid Edge 2020 的 occurrence COM 合同没有可持久化 Suppress 属性；活动配置未枚举的实例自然不进入清单，
  不把会话加载状态 `Activate` 误判为抑制，以免静默丢件。下游合同仍保留 `IsSuppressed` 供显式清单和后续版本使用。

## 已核实环境

- Solid Edge 2020 SDK：`C:\Program Files\Siemens\Solid Edge 2020\SDK\sesdk.chm`
- Solid Edge 官方批处理示例：
  `C:\Program Files\Siemens\Solid Edge 2020\Custom\Batch`
- SolidWorks COM ProgID：`SldWorks.Application`
- 当前工作站 SolidWorks：`33.5.0.0053`（SolidWorks 2025 SP5）
- SolidWorks 官方 Interop：
  `C:\Program Files\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS\api\redist`

安装路径和版本只用于当前预研记录；实现必须通过 COM 注册和运行时探测发现环境，不得硬编码上述路径。
