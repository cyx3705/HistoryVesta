# SE2SW 格式转换模块

> 状态：3.6.2 —— 普通 `.par` 的 FeatureWorks 参数不再包含钣金位；错误特征树会被丢弃并重新导入为哑实体。
> 目标宿主：OneHistoryStudio / AppShell 模块宿主  
> 目标格式：Solid Edge `.par/.asm` -> Parasolid `.x_t` -> SolidWorks `.SLDPRT/.SLDASM`

SE2SW 是 019 Studio 工具箱中的 OHS UI 模块，使用 Solid Edge COM 导出 Parasolid，再使用 SolidWorks COM
导入并保存为 SolidWorks 零件或装配体。V3.6.0 将普通工具窗口 `se2sw` 收敛为一个来源自适应页面：点击顶部
来源段后选择单个 `.asm`，即自动解析并进入装配流程；选择文件夹，则扫描顶层 `.par` 并批量转换全部尚无产物的
零件。OHS 兼容实现继续保留在源码和协议中，但当前页面不提供入口。V3.3 起装配**按源装配的层级生成嵌套 `.SLDASM`**：每个唯一 `.par`
各转换一次，每个唯一子装配生成自己的 `.SLDASM`，父级把它当一个组件插入；全部组件固定，不翻译配合。

嵌套所需的局部矩阵不靠世界矩阵求逆换算，而是把每个子装配 `.asm` 作为独立顶层文档打开后直接读取——
读到的本来就是该文档坐标系下的值。转换前用世界矩阵与局部矩阵互相印证（阈值 1e-9），
参考系用错会在触碰 CAD 之前被拦下。

V3.5 起可选开启**装配关系重建**：把 Solid Edge 的 `Relations3d` 翻译成 SolidWorks 配合。
关系按所属 `.asm` 落到各自那层，实体靠几何唯一定位而非坐标点选，配合逐条建立并立即校验位置——
相对 V3.3 基线漂移超过 1e-6 m 就回滚该条并还原位置。未被任何配合约束的组件保持固定，
**位置精度绝不退化**，建不起来的逐条报到界面上。解析到可翻译关系时默认开启；与特征识别不互斥。

V3.5.2 起，开启特征识别时每个唯一零件由独立子 Worker 处理，并在附着既有 SolidWorks 单实例时
逐件重置 FeatureWorks 加载项。一个零件的 `RPC_E_SERVERFAULT` 不再污染后续导入或装配构建；
未开启识别的路径仍使用原批量导入。

V3.5.3 修复 COM 返回早于 `SLDWORKS.exe` 进入进程表时的所有权竞态。全新会话不再在首个文档前
无意义地卸载/重载 FeatureWorks；模块明确创建的隐藏 CAD 若在 `ExitApp` 后超时，只按记录的唯一 PID
回收，绝不按进程名清理，也绝不关闭启动前已有的用户会话。零件阶段失效时装配会明确中止，
不会继续消费损坏会话或生成误导性的缺件装配。

V3.6.2 将普通零件自动识别掩码从包含钣金能力的 `0x3FF` 收紧为机械特征六位 `0x3F`。`CreateFeatures`
返回成功后仍会读取一级特征树；出现钣金类型、残留任何导入体或无法读取特征树时，当前文档不会保存，而是从
原 XT 重新导入几何完整的哑实体，并通过 `FeatureRecognitionSemanticMismatch` 单独报告。自动调用前清空
“本地识别实体”，不再错误预选任意首面。`XJ10B-12导热块` 已得到手工等价树；`消解加热板` 的自动树仍
不等价于手工结果，当前必须安全降级为哑实体，不能记为识别成功。几何守卫使用 `2e-5` 体积相对偏差上限。

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
tools/MateMatchProbe   SE 关系几何到 SW 面的只读匹配率探针
tools/MateApplyProbe   AddMate5 应用姿势的最小复现矩阵探针
tools/PartGeometryProbe  以 XT 为基准核对 SLDPRT 体积/面数的几何守卫探针
tools/FeatureWorksProbe  FeatureWorks 可用性分步诊断探针
tools/MultiInstanceProbe SolidWorks 多实例并存与 FeatureWorks 独立可用性探针
tools/FeatureWorksBatchSmoke  多零件单批次的非交互式 FeatureWorks 隔离门禁
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
  会话刷新与首件重新导入恢复；其中通用种子面结论已由 V3.6.2 真机门禁推翻
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
- [V3.5：装配关系重建](docs/25-V3.5-装配关系重建.md) —— 设计合同；关系采集、几何匹配、
  类型映射与校验回滚；含 [tools/MateMatchProbe](tools/MateMatchProbe)（A 路裁决：两轮实测均 100%）
- [特征识别几何损坏与 FeatureWorks 不可用](docs/28-特征识别几何损坏与FeatureWorks不可用.md) —— 识别重建实体后
  从不校验几何导致零件变方块；已加几何守卫与重导入还原（待真机验证）
- [V3.5.2：FeatureWorks 调用隔离修复](docs/29-V3.5.2-FeatureWorks调用隔离修复.md) —— 单零件子 Worker、
  加载项重置、故障重试、取消/事件转发与非交互式真机门禁
- [V3.5.3：装配失效与 CAD 生命周期修复](docs/30-V3.5.3-装配失效与CAD生命周期修复.md) —— 所有权竞态、
  全新/既有会话分流、自有进程故障回收与装配组合门禁
- [V3.5.5：面指针跨重建失效与门禁盲区](docs/32-V3.5.5-面指针跨重建失效与门禁盲区.md) —— 缓存的 Face2
  在 ForceRebuild 后失效导致识别开启时配合大面积丢失；新增识别+配合组合档与文档泄漏断言
- [V3.6.1：特征识别超时降级](docs/36-V3.6.1-特征识别超时降级.md) —— 识别子 Worker 三分钟无进度后隔离终止，使用普通导入管线重建哑实体并继续装配
- [V3.6.2：FeatureWorks 识别参数错误](docs/38-V3.6.2-FeatureWorks识别参数错误.md) —— 普通零件掩码隔离钣金位、结果语义守卫与干净重导入降级
- [V3.6.0：单页来源自适应](docs/35-V3.6.0-单页来源自适应.md) —— 统一来源入口、文件夹顶层批量转换、装配/零件状态隔离与验收记录
- [V3.5.7：装配探查自动化](docs/34-V3.5.7-装配探查自动化.md) —— 选择 `.asm` 后自动探查，移除重复操作与提示栏；记录 OHS 控制台日志契约边界
- [V3.5.6：页面布局更新](docs/33-V3.5.6-页面布局更新.md) —— 转换选项横向展开，装配页合并输入/输出路径并支持直接打开目录
- [V3.5.4：组件文档泄漏导致配合丢失](docs/31-V3.5.4-组件文档泄漏导致配合丢失.md) —— V3.3 回归：
  嵌套生成器不关组件文档，撞上同名已打开后组件与配合整块消失；含修复与文件级核对
- [V3.6.3：性能与零件级并行](docs/39-V3.6.3-性能与零件级并行.md) —— 耗时结构实测、固定开销回收、
  SolidWorks 多实例并行可行性（3/3 实例 + FeatureWorks 3/3 可用）；含 [tools/MultiInstanceProbe](tools/MultiInstanceProbe)
- [V3.5.0：发布收口](docs/27-V3.5.0-发布收口.md) —— 三档真机门禁、版本单一真源验证、正式槽备份与哈希
- [V3.5：配合应用阻塞问题报表](docs/26-V3.5-配合应用阻塞问题报表.md) —— **已结案**：根因是
  swAddMateError_NoError = 1 被当作失败；含证据链、三处修复与终局门禁（56/56 落位、偏差 2.43e-9 m）
- [V3.3：子装配嵌套链路](docs/20-V3.3-子装配嵌套链路.md) —— 设计合同与实施记录；每个子装配递归生成
  `.SLDASM`、局部矩阵采集与自校验、装配树标注、三层真机门禁
- [V1.1 工程实施与验收](docs/05-V1.1-工程实施与验收.md)

- [V3.3.1：质量更新设计](docs/21-V3.3.1-质量更新设计.md) — 版本、路径、协议与进度语义的单一真值治理
- [V3.3.1：实施与发布收口](docs/22-V3.3.1-实施与发布收口.md) — 门禁、正式槽备份、运行时认证和全量哈希核验
- [V3.3.2：计数与状态质量修复](docs/23-V3.3.2-计数与状态质量修复.md) — 复用装配的可审计计数、嵌套抑制件统计与用户可见文案治理
- [V3.3.2：发布收口](docs/24-V3.3.2-发布收口.md) — 自动化门禁、正式槽备份、模块注册与哈希认证

## V3.0 范围

- 零件窗口支持批量 `.par`；装配窗口支持单个 `.asm`，V3.0 装配只开放外界模式。
- 中间格式固定使用文本 Parasolid `.x_t`；界面可简称为“XT”。
- 输出支持 `.SLDPRT`，并把 `.asm` 输出为全部组件固定的 `.SLDASM`（V3.3 起按源层级嵌套，见下）。
- 不处理钣金 `.psm`、工程图 `.dft`；装配引用中的非 `.par/.asm` 文件跳过并报告。
- 同一批次串行转换，避免两个 CAD 应用的 COM 自动化并发冲突。
- 不翻译 Mates、材料、属性或配置；隐藏件仍插入并报告。

## V3.5 范围

- 三种已实测的关系类型：平面（重合 / 距离）、轴（同轴 / 距离）、接地（固定组件）；
  其余类型报 `MateTypeUnsupported` 并带回接口名，不猜测映射。
- 对齐一律 `swAlignCLOSEST`：组件已精确位于源位置，最近解就是不动。
- 每加一条配合立即校验全部组件相对基线的漂移，超差即删配合**并还原位置**。
- 关系只翻译到它自己那一层的 `.SLDASM`，不跨层提升。
- 与特征识别可同时开启——实测识别后几何匹配率仍为 100%。

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
