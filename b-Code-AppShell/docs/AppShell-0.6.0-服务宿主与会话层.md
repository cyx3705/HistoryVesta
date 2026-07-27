# AppShell 0.6 系列 · 服务宿主与会话层 —— 框架工程实施文档

> 文档性质:框架能力升级(**全部加法**,现有装配路径与派生应用零破坏)
> 基线版本:AppShell 0.5.0(首次固定版本包)｜ 目标:**0.6.0**(服务宿主 + UI 模块)→ **0.6.1**(会话层 + 协议协商)
> 日期:2026-07-26 ｜ 状态:**0.6.0/0.6.1 已实施(正式本机部署验收通过；机器重启未测)**
> 交付节奏:0.6.0 与 OHS V2.5.0 同步交付,0.6.1 与 OHS V2.6.0 同步交付,**严格串行**。
> **版本区间约定(用户裁决)**:整个 **0.6.n 区间预留给 OHS 服务化演进期的框架增量**——
> OHS 后续版本若还需要框架改动,依次占用 0.6.2、0.6.3…,每个增量一份文档、
> 完整走 0.5.0 发布链;0.7.0 留给下一个框架级主题。
>
> 派生侧驱动文档(需求与验收场景在派生侧,框架契约在本文):
> - [OHS 32-V2.5.0-前后端分离与桌面活动坞](../../b-Office/versions/32-V2.5.0-前后端分离与桌面活动坞.md)
> - [OHS 33-V2.6.0-Web服务与会话层](../../b-Office/versions/33-V2.6.0-Web服务与会话层.md)
>
> 相关框架文档:[版本记录](AppShell版本记录.md) · [0.5.0 发布与质量整备](AppShell-0.5.0-发布与质量整备.md) ·
> [二次开发演进手册](二次开发演进手册.md) · [需求规划 README](通用窗口框架模板_需求规划_README_3.md)

---

## 0. 一句话定义

0.6 系列让 AppShell 从「单进程 WPF 框架」升级为「**可拆分宿主**框架」:
派生应用可以选择把命令总线/数据/MCP/模块跑进一个常驻的**无窗服务进程**,
WPF 窗口层退为可开可关的客户端;同时模块契约获得 **UI 模块**能力,
网关获得**会话身份**与**真实的协议协商**。现有单进程装配路径原样保留——
对 WBall 与演示宿主,0.6 只是「多了能力,没变行为」。

---

## 1. 设计原则(全系列铁律)

1. **加法原则**:`ShellWindow` 一体装配路径一行不动;新能力全部走新装配点。
   0.4.4 反哺(Services 零 WPF 依赖、`IMcpAuditLog`、SyncContext 注入)是本系列的地基,
   不得回退;
2. **命令是唯一契约**:进程拆分不发明第二种 IPC 语义——前端接入点收命令文本、
   回 `CommandResult` 三元组,与 MCP 网关共享策略/审计/确认管线;
3. **派生零感知升级**:不使用新装配点的派生应用,重编译到 0.6.x 后行为与快照
   必须与 0.5.0 逐条一致(验收硬判据);
4. **框架不认识 OHS**:活动坞、`proj.*` 等派生货色不进框架;框架只提供
   ServiceHost / IUiModule / ClientSession 等通用契约(0.5.0 已完成的
   通用化收口不得倒退)。

---

## 2. 0.6.0 · 服务宿主与 UI 模块

### 2.1 ServiceHost(无窗 WPF 宿主)

- 新增**独立第四工程** `AppShell.ServiceHost`(用户裁决:`AppShell.Services` 在 0.4.4
  刚解耦到零 WPF 依赖,朝 V3.0 方向**不许回沾**;包矩阵 3→4,0.5.0 发布链扩一格):
  `ServiceHost.Run(ServiceComposition)`——以 `ShutdownMode.OnExplicitShutdown` 启动
  WPF `Application`,**不创建任何窗口**,持有 Dispatcher 供 ModuleHost/UI 模块/确认弹框使用;
- **确认弹框随 ServiceHost 走**(用户裁决):`RemoteConfirmDialog` 由服务进程在自己的
  Dispatcher 上弹出,危险命令的人工裁决**不依赖前端在场**——headless MCP 场景下
  批准链完整,不存在「无前端即拒绝」的降级;
- **为什么是无窗 WPF 而非控制台**:UI 模块与宿主确认框需要 UI 线程与桌面;
  也因此明确 **不做 Windows Service(SCM)**——session 0 无桌面(全系列排除项);
- 单实例保护:named mutex,重复启动直接退出并提示;
- 启动分层契约:宿主保证「总线+网关就绪」不等待任何派生侧重活;
  重活(扫描/索引类)由派生应用自行惰性化,框架提供 `IDeferredStartupWork`
  登记通道(就绪后台顺序执行,失败只告警)。

### 2.2 装配点拆分

| 装配点 | 内容 | 消费者 |
|---|---|---|
| `ServiceComposition` | 总线/数据/设置/日志/MCP 网关/提示词治理/ModuleHost/前端接入点 | 服务进程 |
| `ShellComposition` | 窗口/停靠/控制台/面板/资源窗口(现 ShellWindow 全部内容) | 前端进程或单进程应用 |
| 单进程路径(既有) | ShellWindow 一体装配 = 两者合体 | WBall、演示宿主、不迁移的派生 |

### 2.3 UI 模块契约

- manifest 增可选 `ui: true`;模块可实现 `IUiModule { CreateUi(); DestroyUi(); }`,
  框架在 Dispatcher 上调用,生命周期随模块装载/卸载/热重载;
- 命令模块契约不变,现有模块(ToolKit/ProjectPulse/ToolRelay 等)零改动;
- UI 模块热重载语义:先 `DestroyUi` 再卸载,重载后重建——判据:重载 10 次无句柄泄漏。

### 2.4 前端接入点与 `svc.*`

- `POST /api/command` `{text}` → `{success, message, data}` + `WS /api/events`
  (日志流/UI 命令中继/确认请求),默认 127.0.0.1,端口与 token 走设置;
- UI 命令中继契约:描述符可标 `ExecutionSite = Frontend`,服务侧执行时经
  WS 转发已连接前端;无前端 → 返回明确失败结果(非异常非超时);
- 新增 `svc.status / svc.stop / svc.restart / svc.autostart`(自启动 = HKCU Run 键,用户级;
  **缺省开**——部署/服务首启即注册,`svc.autostart off` 显式退出。用户裁决:服务终态
  是无感 CI/CD 飞轮的一环,不能靠人记得去开)。

### 2.5 0.6.0 验收(框架侧)

- Debug/Release 0 警告;演示宿主**单进程路径** GUI 实跑(证明加法无损);
- 新增演示:最小服务宿主 + 最小 UI 模块(演示用,非 OHS 活动坞)跑通
  「服务启动 → 模块 UI 出现 → 热重载 → svc.stop」;
- 不迁移派生零感知判据:OHS 以旧路径构建,冒烟 12/12、MCP 快照与 0.5.0 基线逐条一致;
- 隔离 PackageSmoke(0.5.0 建立的通道)对 0.6.0 包复跑。

---

## 3. 0.6.1 · 会话层与协议协商

> 需求背景与实测证据见 OHS 31 号文档 §1.7/§1.9(banana 探针、`_clientName` 串话),
> 验收场景见 [33 号文档](../../b-Office/versions/33-V2.6.0-Web服务与会话层.md)。此处只记框架契约。

### 3.1 ClientSession

- `ClientSession { Id, Kind(MCP|Shell|Web), Name, ProtocolVersion, ConnectedAt }`;
- 网关实例级 `_clientName`/`_protocolVersion` 字段**废除**,归属迁入会话;
- `IMcpAuditLog` 增会话参数重载(旧签名保留转发,包 API 兼容——0.5.0 起有
  package validation,破坏性签名变更会被打包关卡拦下,这是有意的双保险);
- 审计留痕文本格式不变(`MCP:名称`),仅名称来源改为会话。

### 3.2 协议协商

- `SupportedProtocols = ["2025-06-18", "2025-03-26"]`——**清单是承诺**:
  无 SSE 传输就不许写 `2024-11-05`(OHS 31 号 R2 审查教训,升格为框架规矩);
- `initialize` 清单内回显、清单外回最新并告警;
- `MCP-Protocol-Version` 头:无效 → 400;缺失 → 按 `2025-03-26`(轻客户端如
  ToolRelay 不握手,靠此降级语义存活,回归必测);
- Web 档硬化(token 必选/绑定策略/CORS/限流/远程确认开关)的**机制**进框架,
  **策略缺省**由派生配置——框架缺省一律取最安全值。

### 3.3 0.6.1 验收(框架侧)

- 并发串名探针转绿(两客户端交替握手+调用,审计逐条归属正确);
- 协商矩阵逐格留证(两版回显/`banana` 回最新/头无效 400/缺头降级);
- 纯内部重构部分(会话)线上零变化:MCP 快照与 0.6.0 基线逐条一致;
- 包 validation 通过(无破坏性 API 变更)。

---

## 4. 排除项(全系列)

| 项 | 理由 |
|---|---|
| Windows Service(SCM) | session 0 无桌面,UI 模块/确认框即死 |
| HTTP+SSE 旧传输 | 无消费者;连带排除宣布 `2024-11-05` |
| `outputSchema` | 沿 OHS 31 号 DQ245-3,独立立项 |
| TLS | localhost/受控局域网场景推迟,绑定策略文档标注明文风险 |
| Web 前端 | 框架永远不含页面;那是派生应用的货 |

---

## 5. 版本与交付纪律

- 版本真值沿 0.5.0 机制(中央版本/锁文件/包身份),0.6.n 每个增量各自完整走
  0.5.0 建立的发布链(pack/validation/PackageSmoke/manifest/SHA-256);
- 交付后在[版本记录](AppShell版本记录.md)各加一行(交付时写,规划期不预写);
- 与 OHS 版本严格配对:0.6.0↔V2.5.0、0.6.1↔V2.6.0,后续增量在各自文档中声明配对;
  任一侧验收未过,配对侧不得交付;
- **0.6.n 区间纪律**:该区间只收「OHS 服务化演进」主题的增量;不相关的框架改动
  等 0.7.0,防止区间语义稀释;
- 执行者纪律同 OHS 32 号 §7(含 CET 环境变量注意)。

## 6. 交付记录(2026-07-26)

- 四工程/四包矩阵落地，版本真值 0.6.1；Debug/Release 0 警告。
- 0.6.0：ServiceHost、ServiceComposition、mutex、服务确认、`svc.*`、Frontend 路由、
  WebGateway/ShellServiceClient、`IUiModule`/`ModuleCommandAttribute` 和延迟启动完成。
- 0.6.1：ClientSession、会话审计兼容重载、双协议协商、版本头校验、Web token/绑定/CORS/
  限流/事件/确认闭环完成。
- 0.6.1 staging 生成 Core/Services/Shell/ServiceHost 的 nupkg+snupkg，包内文档、身份、
  演示 publish、漏洞审计与隔离 PackageSmoke 全部通过；未执行正式 `-Publish`。
- 旧 ShellWindow 单进程路径由原六套 OHS Smoke 回归；新增 ServiceWeb 套件覆盖服务化协议。
- 按用户要求未运行最小服务宿主 GUI、UI 模块热重载 10 次、Run 键、登录/重启或正式服务进程。

—— 文档结束 ——
