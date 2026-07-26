# AppShell 0.5.0 · 首次版本包发布与质量整备 —— 工程实施文档

> 文档性质：发布工程 + 依赖安全 + 框架边界清理，不新增业务功能  
> 基线版本：AppShell 0.4.4｜目标版本：AppShell 0.5.0｜日期：2026-07-26｜状态：待审查  
> 发布范围：仓库内本地 NuGet feed；**不推送 NuGet.org，不创建 Git tag，不提交或推送 Git，除非用户另行明确授权**

---

## 0. 一句话定义

0.5.0 是 AppShell 从“可被 OHS 源码引用的框架组件”走向“可被伞外项目按固定版本消费”
的第一版：统一版本和包身份，修掉当前会产生错误包的发布配置，清除高危传递依赖，补齐
可重复打包、校验、隔离消费与演示程序发布闭环，同时收口仍会泄漏到通用框架界面的 OHS 专有文案。

本版不改变 OHS 的引用方式。020 内的 OHS 继续通过 `ProjectReference` 使用
`b-Code-AppShell` 唯一源码；NuGet 包只服务未来的伞外消费项目。

---

## 1. 发布前基线取证

### 1.1 当前构建基线

2026-07-26 实测：

| 项目 | Debug | Release |
|---|---:|---:|
| `b-Code-AppShell/AppShell.sln` | 0 警告 / 0 错误 | 0 警告 / 0 错误 |

当前解决方案包含四个工程：

| 工程 | 定位 | 0.5.0 发布方式 |
|---|---|---|
| `AppShell.Core` | 纯契约、指令核心、MCP 元数据 | NuGet 库包 |
| `AppShell.Services` | SQLite、设置、日志、MCP、模块托管 | NuGet 库包 |
| `AppShell.Shell` | WPF 外壳、停靠窗口、内置 UI | NuGet 库包 |
| `App` | 独立演示宿主 | **不打 NuGet 包**；发布 framework-dependent ZIP |

### 1.2 当前打包结果是错误的

直接对四个工程执行 `dotnet pack -c Release` 的实测结果：

| 当前产物 | 问题 |
|---|---|
| `AppShell.Core.1.0.0.nupkg` | 库未继承演示宿主的 0.4.4；版本错误 |
| `AppShell.Services.1.0.0.nupkg` | 版本错误；包描述仍为 `Package Description` |
| `AppShell.Shell.1.0.0.nupkg` | 版本错误；包描述仍为 `Package Description` |
| `AppShell.0.4.4.nupkg` | 把演示 `WinExe` 当成库包发布；不应存在 |

四个包全部缺少 package README；没有 `.snupkg`；没有稳定的包 ID、许可证决策、版本清单、
校验和或隔离消费测试。当前 `z-Package-AppShell` 只是目录骨架，不能称为已建立发布流程。

### 1.3 依赖安全阻断

`dotnet list AppShell.sln package --vulnerable --include-transitive` 当前报告：

| 包 | 当前版本 | 严重级别 | 公告 |
|---|---:|---:|---|
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.6（由 `Microsoft.Data.Sqlite 8.0.10` 传递引入） | High | `GHSA-2m69-gcr7-jv3q` |

该公告覆盖 `SQLitePCLRaw.lib.e_sqlite3 <= 2.1.11`，且 2.x 包线上没有修复版本。
只把 `Microsoft.Data.Sqlite` 从 8.0.10 升到 8.0.29 仍会解析到旧的 bundle 2.1.6，
**不能消除漏洞**。0.5.0 必须同时切到 SQLitePCLRaw 3.x 的新 bundle，并以审计清零为准，
不能只看顶层版本号。

### 1.4 框架边界仍有可见遗留

0.4.4 把 OHS 能力上抛后，仍有三类派生应用内容进入了通用框架表面：

1. `CommandManualGenerator` 把标题写死为 `OneHistoryStudio 命令手册`，并在生成文件中写死
   `b-Office/meta/命令手册.md`；
2. `mcp.schema`、`mcp.parse`、提示词治理等框架命令仍使用 `proj.*` 作为公开示例；
3. 通用 `ModulesView` 无条件显示“扫描项目库工具 / 同步全部工具 / 移除所选工具”，但独立
   AppShell 演示宿主没有 OHS 的 `tool.scan`、`tool.sync`、`tool.remove` 指令，按钮是死入口。

历史文档与代码注释可以保留反哺来源；**运行时 UI、生成物和公开命令示例必须通用化**。

### 1.5 严格分析器现状

额外以 `AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild=true` 审计，当前产生
113 条警告。它们混合了三种性质：

- 真正值得修复的确定性和分配问题，如区域性格式化、重复创建 `JsonSerializerOptions`；
- 低收益性能建议，如把私有返回类型从接口改成具体类型；
- 会破坏既有公共 API 的命名建议，如重命名 `ISettingsService.Get/Set`、`ParamType.Int/Double`。

0.5.0 **不以“清空 113 条”为目标**，也不为取悦分析器改公共 API。本版只处理不改变契约的
高信号项，并保留普通 Debug/Release 的 0 警告铁律。完整分析器基线与公共 API 治理另行立项。

### 1.6 当前未提交 MCP 改动是前置输入

`AppShell.Services/Mcp/McpGateway.cs` 当前已有未提交的 `structuredContent` 加法改动，来源于
OHS V2.4.5 的 31 号实施文档。0.5.0 不覆盖、不重写该成果，但发布基线冻结前必须先完成
31 号文档的 M0-M4 验收，确认旧 `content` 字节不变、ToolRelay 1.0.1 闭环和客户端探针取证。

若 31 号施工未完成，0.5.0 **不得进入打包阶段**。

---

## 2. 发布决策

### 2.1 版本与兼容性

| 项 | 决定 |
|---|---|
| 版本 | `0.5.0`，三个库包与演示宿主完全一致 |
| 运行时 | .NET 8；本版不跨到 .NET 10 |
| 桌面平台 | Windows；演示包固定 `win-x64`、framework-dependent |
| OHS 伞内引用 | 继续 `ProjectReference`，不改成 `PackageReference` |
| 公共 API | 不做破坏性重命名；0.5.0 成为未来包兼容性比较的首个基线 |
| MCP | 收录已验收的结构化结果加法；协议版本协商与跨客户端状态串话不在本版扩做 |

### 2.2 包 ID 与依赖图

首次正式定名如下。包 ID 与程序集名可以不同，程序集和命名空间继续保持 `AppShell.*`：

```text
OneHistory.AppShell.Shell 0.5.0
  -> OneHistory.AppShell.Services 0.5.0
       -> OneHistory.AppShell.Core 0.5.0
       -> Microsoft.Data.Sqlite 8.0.29
       -> SQLitePCLRaw.bundle_e_sqlite3 3.0.4
  -> OneHistory.AppShell.Core 0.5.0
  -> Dirkster.AvalonDock 4.72.1
  -> Dirkster.AvalonDock.Themes.VS2013 4.72.1
```

`AppShell.Shell` 是完整框架消费者的单一入口；NuGet 会传递带入 Services 和 Core。
Core、Services 仍单独发布，允许不需要 WPF 外壳的消费者只取下层能力。

AvalonDock 4.72.1 当前没有安全公告，且停靠布局和主题资源键属于高风险冻结区。本版不为
“追最新版”升级到 4.74.1；AvalonDock 升级必须带布局兼容专项，留到独立版本。

### 2.3 本地发布，不公开推送

0.5.0 只发布到仓库内 `z-Package-AppShell/feed/`，这是一次可复现的固定版本交付，
不是 NuGet.org 公共发行。

原因：仓库当前没有 `LICENSE` / `NOTICE`，不能由施工者擅自替用户选择 MIT、Apache、私有许可
或其他授权方式。包可在本地 feed 内验证和供自有项目消费；公开推送前必须另行完成：

1. 用户明确选择许可证；
2. 包内包含对应许可证文件和元数据；
3. 确认包 ID 在目标源可用；
4. 用户明确授权 push 与 tag。

发布脚本**不得包含 NuGet.org API key，也不得自动调用外部 `dotnet nuget push`**。

### 2.4 正式产物

`z-Package-AppShell/feed/`：

```text
OneHistory.AppShell.Core.0.5.0.nupkg
OneHistory.AppShell.Core.0.5.0.snupkg
OneHistory.AppShell.Services.0.5.0.nupkg
OneHistory.AppShell.Services.0.5.0.snupkg
OneHistory.AppShell.Shell.0.5.0.nupkg
OneHistory.AppShell.Shell.0.5.0.snupkg
AppShell-Demo-0.5.0-win-x64-framework-dependent.zip
```

配套元数据：

```text
z-Package-AppShell/changelog/0.5.0.md
z-Package-AppShell/manifest/0.5.0.json
z-Package-AppShell/checksums/0.5.0.sha256
```

---

## 3. 变更明细

### 3.1 版本真值与包元数据

在 `b-Code-AppShell/Directory.Build.props` 建立 AppShell 子树的版本真值，并显式导入 020 根级
`Directory.Build.props`，不能因为新增子级 props 丢失 `TreatWarningsAsErrors`：

```xml
<Project>
  <Import Project="..\Directory.Build.props" />
  <PropertyGroup>
    <VersionPrefix>0.5.0</VersionPrefix>
    <Version>0.5.0</Version>
    <AssemblyVersion>0.5.0.0</AssemblyVersion>
    <FileVersion>0.5.0.0</FileVersion>
    <InformationalVersion>0.5.0</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <Authors>OneHistory</Authors>
    <Company>OneHistory</Company>
    <RepositoryType>git</RepositoryType>
    <RepositoryUrl>https://github.com/cyx3705/OneHistory</RepositoryUrl>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

随后：

1. 从 `src/App/App.csproj` 删除局部 `<Version>0.4.4</Version>`，避免第二真值；
2. App 工程设 `<IsPackable>false</IsPackable>`；
3. 三个库工程分别声明唯一 `PackageId`、准确 `Description`、`PackageTags`；
4. 三个库生成 XML 文档并随包进入 `lib/<tfm>/`；缺失 XML 注释暂不作为 0.5.0 阻断；
5. 增加共享 `PACKAGE.md` 并设为三个包的 `PackageReadmeFile`；
6. 使用 `Microsoft.SourceLink.GitHub`（`PrivateAssets=All`）、portable PDB、
   `IncludeSymbols=true`、`SymbolPackageFormat=snupkg` 生成符号包；
7. 开启 package validation；0.5.0 是首个基线，不设置不存在的历史包作 baseline。

包 ID：

| 工程 | PackageId | TFM |
|---|---|---|
| `AppShell.Core.csproj` | `OneHistory.AppShell.Core` | `net8.0` |
| `AppShell.Services.csproj` | `OneHistory.AppShell.Services` | `net8.0` |
| `AppShell.Shell.csproj` | `OneHistory.AppShell.Shell` | `net8.0-windows` |

### 3.2 依赖版本集中与高危依赖清除

新增 `b-Code-AppShell/Directory.Packages.props`，开启 Central Package Management，移除各 csproj
内散落的 `Version=`：

| 包 | 目标版本 | 理由 |
|---|---:|---|
| `Microsoft.Data.Sqlite` | 8.0.29 | 留在 .NET 8 servicing 线，替代 8.0.10 |
| `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.4（Services 直接引用） | 强制离开存在高危公告的 `lib.e_sqlite3` 2.x 原生包族 |
| `Dirkster.AvalonDock` | 4.72.1 | 布局冻结区，本版不升级 |
| `Dirkster.AvalonDock.Themes.VS2013` | 4.72.1 | 与主包严格同版 |
| `Microsoft.SourceLink.GitHub` | 8.0.0 | 生成可调试符号包，不传递给消费者 |

必须提交 `packages.lock.json`，发布脚本用 locked mode 恢复。验收不以“能 restore”为终点，
而以以下三条同时成立为终点：

1. `dotnet list ... --vulnerable --include-transitive` 无漏洞；
2. 依赖图中不再出现 `SQLitePCLRaw.lib.e_sqlite3 2.x`；
3. SQLite 创建、插入、查询、更新、导出与演示宿主 GUI 实跑均通过。

若 SQLitePCLRaw 3.0.4 与 `Microsoft.Data.Sqlite 8.0.29` 的实际运行验证失败，0.5.0 停止发布，
不得通过关闭 NuGetAudit 或忽略 NU1903 强行出包。

### 3.3 通用框架边界清理

只清运行时可见面，不抹历史来源：

1. `CommandManualGenerator` 标题改为 `${AppIdentity.Current.Name} 命令手册`；更新提示改成
   通用的 `command.manual file=<相对 Markdown 路径> apply=true`，不出现 `b-Office`；
2. AppShell 自带 MCP/治理命令的 `proj.*` 示例换成框架内真实存在的 `db.query`、`help`、
   `module.list` 等示例；不得假造新命令；
3. `ModulesView` 仅在注册表同时存在相应命令时显示 `tool.scan` / `tool.sync` / `tool.remove`
   操作；独立演示宿主隐藏死入口，OHS 注册这些命令后保持现有按钮；
4. `McpGateway` 当前新增结构化结果的源码注释改用 AppShell 0.5.0 和通用兼容性表述，
   具体的 OHS 019 ToolRelay 依赖留在实施/版本文档，不写进通用库源码注释。

出口：全仓扫描 `b-Code-AppShell/src` 后，允许历史性注释出现 OneHistory/OHS，
但所有 UI 可见文本、自动生成文档、公开 Schema 示例均不再要求消费者认识 OHS 项目域。

### 3.4 小范围代码质量修正

本版只做不改变公共签名与指令语义的修正：

1. `SettingsService` 缓存单例 `JsonSerializerOptions`，不在每次落盘时重新分配；
2. 写入设置、MCP 端口/超时和日志日期等机器持久化值时使用 `InvariantCulture`；读取对应整数
   同样使用 invariant 口径，避免系统区域变化后配置不可回读；
3. 命令手册中数量、页码等机器生成值使用 invariant 格式，保证跨区域生成哈希稳定；
4. 清理本次改动触发的分析器告警，但不重命名 `ISettingsService.Get/Set`、`ParamType.Int` 等
   已公开契约；
5. 不以拆分 `BuiltinCommands.cs` / `DockingHost.cs` 等大文件制造无关 diff。大文件治理单独立项。

### 3.5 可重复发布脚本

新增 `b-Code-AppShell/eng/Publish-AppShell.ps1`。脚本接受明确版本参数，但只允许与版本真值一致
的 `0.5.0`，按以下固定步骤执行：

1. 检查 `b-Code-AppShell` 源码已经形成提交且该路径工作树干净；拒绝从未提交、未验收的
   混合状态生成正式 0.5.0；
2. 在隔离临时目录设置 `NUGET_PACKAGES`，以 locked mode restore；
3. 构建 AppShell Debug/Release，均 0 警告 0 错误；
4. 运行依赖漏洞审计，不允许 `--no-audit` 或 `NuGetAudit=false`；
5. 只 pack Core、Services、Shell，断言没有生成 `AppShell.0.5.0.nupkg`；
6. 逐个检查 nuspec 的 ID、版本、TFM、依赖版本、README、repository commit 和 XML 文档；
7. 从本地 feed、隔离缓存恢复 `tests/PackageSmoke`，不允许回退到项目引用；
8. `dotnet publish src/App` 为 win-x64 framework-dependent，压缩演示 ZIP；
9. 对全部 `.nupkg`、`.snupkg`、`.zip` 生成 SHA-256；
10. 生成 manifest，记录版本、源 commit、构建 SDK、TFM、RID、包依赖、文件名和哈希；
11. 原子替换 `z-Package-AppShell` 对应的 0.5.0 产物；中途失败不得留下半套正式文件。

脚本不负责 commit、tag、push 或外部 NuGet push。这些动作必须在产物验收后由用户单独授权。

### 3.6 隔离包消费者

新增 `b-Code-AppShell/tests/PackageSmoke`，但不加入日常 `AppShell.sln`，避免开发构建依赖尚未
生成的本地包。它是一个最小 WPF 派生应用，只引用：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="0.5.0" />
```

测试时使用隔离 `NUGET_PACKAGES` 和明确的 `RestoreSources`：当前 0.5.0 本地 feed + nuget.org。
禁止引用 `../../src/*`、禁止复制 AppShell 源码。它必须证明：

- 只引用 Shell 即可传递获得 Core 与 Services；
- `ShellWindow`、`ShellConfig`、`SettingsService`、`FileLayoutStore` 可正常编译装配；
- 包恢复后的三份 AppShell 程序集版本均为 0.5.0.0；
- 启动窗口、执行 `help`、优雅退出均成功。

---

## 4. 里程碑

### M0 · 冻结真实基线

- 完成 31 号文档 M0-M4，保留结构化结果取证；
- 留档 AppShell/OHS 当前 Debug、Release、Smoke 与 GUI 结果；
- 留档当前错误 pack 的四个文件名和 nuspec，作为修复前证据；
- 留档漏洞审计结果与 `GHSA-2m69-gcr7-jv3q` 依赖链。

出口：没有未验收的 AppShell 源码改动混入发布整备。

### M1 · 依赖安全与框架质量

- 引入 Central Package Management 与锁文件；
- 完成 SQLite 8.0.29 + SQLitePCLRaw bundle 3.0.4 运行验证；
- 完成 §3.3 通用化与 §3.4 小范围质量修正。

出口：AppShell/OHS Debug、Release 0 警告 0 错误；漏洞审计清零；OHS Smoke 全绿；
命令数量和指令语义不变，生成手册只出现预期的标题/通用示例变化。

### M2 · 包工程

- 建立 0.5.0 单一版本真值；
- 完成三库包元数据、README、XML 文档、SourceLink 与符号包；
- App 工程 `IsPackable=false`；
- 完成发布脚本与 PackageSmoke。

出口：精确生成 3 个 `.nupkg` + 3 个 `.snupkg`，不存在演示 EXE 的 NuGet 包；
nuspec 依赖图与 §2.2 完全一致。

### M3 · 隔离消费与演示发布

- 清空隔离 NuGet 缓存，从本地 feed 构建 PackageSmoke；
- 发布并解压演示 ZIP；
- 两者分别 GUI 实跑。

出口：PackageSmoke 不含任何 ProjectReference；演示窗口标题显示 0.5.0；模块页在无 `tool.*`
能力时不显示死入口；三窗口可见、`help`、SQLite、MCP 启停和优雅退出正常。

### M4 · OHS 回归

- 构建 `OHS.sln` Debug/Release，0 警告 0 错误；
- 完整运行当前六套 Smoke（以施工时最新套数/断言数为准，不硬编码旧数字）；
- GUI 验证 OHS 原有模块工具同步按钮仍在且可用；
- 核对 MCP 工具数、指令数、`McpState` 与 M0 基线一致。

出口：包工程没有改变伞内源码引用语义，也没有把通用化修正变成 OHS 功能回归。

### M5 · 本地正式发布

- **前置授权**：用户明确授权提交 0.5.0 源码；形成源码提交后确认 `b-Code-AppShell` 路径干净。
  未获提交授权时可以完成 M0-M4 和 staging 验证，但不得把旧 `HEAD` 写进正式 manifest 冒充
  新源码，也不得向 feed 落位不可覆盖的 0.5.0 正式包；
- 写 `changelog/0.5.0.md`；
- 生成 `manifest/0.5.0.json` 与 `checksums/0.5.0.sha256`；
- 发布脚本在干净临时 staging 中重跑一次并原子落位；
- 对 feed 内每个文件重新计算哈希，与 checksum/manifest 双重一致；
- 更新 `z-Package-AppShell/README.md`、`feed/README.md`、`manifest/README.md`、
  `checksums/README.md` 和 `b-Code-AppShell/docs/AppShell版本记录.md`。

出口：本地 0.5.0 固定版本包可供伞外项目引用。Git commit/tag/push 和 NuGet.org push 仍未执行，
等待用户单独授权。

---

## 5. 验收矩阵

| 类别 | 判据 |
|---|---|
| 编译 | AppShell 与 OHS 的 Debug/Release 全部 0 警告 0 错误 |
| 安全 | `dotnet list package --vulnerable --include-transitive` 无结果；不再解析旧 `lib.e_sqlite3` 2.x |
| 版本 | 三库、演示 EXE、nuspec、manifest、changelog 全部为 0.5.0；程序集为 0.5.0.0 |
| 包数量 | 精确 3 nupkg + 3 snupkg；App/WinExe 不可 pack |
| 包内容 | README、XML 文档、portable PDB/SourceLink、repository commit 齐全；无绝对路径、无 `bin/obj` 杂物 |
| 依赖闭包 | PackageSmoke 只引用 Shell；隔离缓存 restore/build/run 成功 |
| 演示包 | ZIP 解压后在已安装 .NET 8 Desktop Runtime 的 win-x64 机器可运行 |
| 通用性 | 演示宿主无 OHS 专有标题、路径、`proj.*` 示例或不可用工具按钮 |
| OHS 兼容 | ProjectReference 保持；Smoke 全绿；模块工具按钮、MCP、SQLite、布局均正常 |
| 可追溯 | 每个正式文件的 SHA-256 同时出现在 checksum 和 manifest，manifest 记录源 commit 与 SDK |
| 发布边界 | 未 push NuGet.org，未创建 tag，未自动执行 Git 提交或推送 |

---

## 6. 风险与回滚

1. **SQLitePCLRaw 2.x → 3.x 是本版最大运行风险。**必须覆盖数据库初始化、CRUD、CSV、
   重启和部署目录原生文件。任一失败即停止发布；不能回退到有 High 公告的 2.x 后继续出包。
2. **包 ID 一旦被伞外项目采用就形成契约。**0.5.0 发布后不得在补丁版改名；公开推送前再做
   一次 NuGet.org 可用性核查。
3. **中央 props 的继承边界。**`b-Code-AppShell/Directory.Build.props` 必须导入父级 props；
   否则会静默丢失根级 nullable、implicit usings 和 warnings-as-errors 纪律。
4. **NuGet 消费测试必须隔离缓存。**本机已有 ProjectReference 构建输出或全局包可能制造假绿；
   PackageSmoke 使用独立 `NUGET_PACKAGES`，并检查 assets 文件的来源。
5. **OHS 专有入口隐藏必须按能力判断。**不得简单从框架删除后让 OHS 丢失功能；注册表存在
   `tool.*` 时按钮必须继续显示。
6. **许可证是发布授权，不是技术默认值。**本版留在本地 feed；施工者不得自行添加开源许可。
7. **不要顺手升级 AvalonDock 或 .NET。**二者都有独立迁移风险，不与首包发布混版。

回滚按里程碑进行：依赖/质量改动、包工程、正式产物分别保持独立提交候选。未获用户提交授权前
只保留工作树变更；出现阻断时回到上一里程碑，不删除用户已有的 31 号文档或 MCP 改动。

---

## 7. 决策记录

| # | 决定 |
|---|---|
| DQ050-1 | 0.5.0 是首个正式可消费包版本；0.4.4 及以前保留为源码历史，不补造旧 NuGet 包 |
| DQ050-2 | 发布三个分层库包；完整消费者只需引用 `OneHistory.AppShell.Shell` |
| DQ050-3 | 演示宿主设为不可 pack，单独发布 win-x64 framework-dependent ZIP |
| DQ050-4 | OHS 继续源码 ProjectReference；包发布不改变伞内继承关系，不复制源码 |
| DQ050-5 | 本版仅落本地 feed；许可证、NuGet.org push、tag 与 Git push 必须另获授权 |
| DQ050-6 | SQLite 留在 Microsoft.Data.Sqlite 8.x，但用 SQLitePCLRaw bundle 3.x 清除 High 公告；审计不清零则不发布 |
| DQ050-7 | AvalonDock 保持 4.72.1；不把无安全必要的 UI 基础库升级混入发布工程 |
| DQ050-8 | 0.5.0 建立首个公共包基线；不为分析器建议重命名既有公共 API |
| DQ050-9 | 通用化只改运行时可见面；历史文档和反哺来源注释不做无意义清洗 |
| DQ050-10 | 发布脚本只构建、验证并生成本地产物，不执行任何外部发布或 Git 状态变更；正式落 feed 前另行取得源码提交授权，确保 manifest 指向真实源码提交 |

---

## 8. 施工顺序

```text
31 号 MCP 改动完成验收
  -> 依赖安全与通用边界清理
  -> 版本/包元数据
  -> 三库 pack + 符号包
  -> 隔离 PackageSmoke
  -> 演示 ZIP GUI
  -> OHS 全回归
  -> checksum + manifest + changelog
  -> 本地 feed 发布
  -> 等待用户决定 commit/tag/push/公开许可
```

顺序不可倒置：特别是不能先生成正式 0.5.0 文件，再回头修依赖或补测试；同一版本号的正式包
一旦被消费就应视为不可覆盖。

—— 文档结束 ——
