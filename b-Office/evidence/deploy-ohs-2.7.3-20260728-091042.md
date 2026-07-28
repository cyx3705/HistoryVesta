# OneHistoryStudio 2.7.3 正式部署证据

> 部署开始：2026-07-28 09:10:42 +08:00  
> 部署完成：2026-07-28 09:16:17 +08:00

## 部署与回退

- 候选：`stage/ohs-2.7.3-rc3`
- 仓库发布快照：`b-Publish`
- 正式目录：`C:\OneHistory\OneHistory-Push\OneHistoryStudio`
- 旧发布快照：`stage/b-Publish-pre-2.7.3-20260728-091042`
- 旧正式部署：`C:\OneHistory\OneHistory-Push\OneHistoryStudio-rollback-2.7.0-20260728-091042`
- 升级前数据库：`C:\OneHistory\OneHistory-Push\OneHistoryStudio-DatabaseBackups\main-db-predeploy-2.7.3-20260728-091042.db`
- 数据库备份 SHA-256：`980B33611F3EF591B8BFB0AF0815AADC87E1AEC9EF282A31BCCAA0D1838D4E07`

## 文件门禁

- `b-Publish` 与正式目录均为 47 个文件，逐文件 SHA-256 差异 0。
- EXE 数量为 1；`OneHistoryStudio.Service.*` 数量为 0。
- ProductVersion `2.7.3`；FileVersion `2.7.3.0`。
- `OneHistoryStudio.exe` SHA-256：`90711849029D9419439CA05D3339E6ED88D72606B9528EA35429E7117FA10D58`
- `OneHistoryStudio.dll` SHA-256：`44B850CF0B92A5ADA495609BDB658ACAAAFE11F6EF505D3319FC314FF92336C0`
- 正式 EXE 重新生成的 133 条核心命令手册与源文件同哈希：
  `F0EA00BAE1C5C75BD6A32857CDA75CE8B2791E1BD35D314E7DA02A839B34F6C6`。

## 运行时门禁

- UI：PID 16808，标题 `OneHistoryStudio v2.7.3`。
- ServiceHost：PID 14496，同一正式 EXE，参数 `--service-host`。
- 关闭首次 UI PID 5052 后 ServiceHost 继续存活，`/api/health` 仍返回产品版本 `2.7.3`；随后重新打开 UI。
- HKCU Run 已迁移为：
  `"C:\OneHistory\OneHistory-Push\OneHistoryStudio\OneHistoryStudio.exe" --service-host`。
- `help`、`mcp.status`、`web.status`、`proj.list`、`module.list`、`command.list` 全部成功。
- 当前模块环境运行时命令为 146 条：133 条核心命令加 13 条模块命令；加载 5 个模块、列出 38 个工作树。

## 最终收口复核（2026-07-28 09:26:56 +08:00）

- `b-Publish` 与正式目录仍各为 47 个文件，逐文件 SHA-256 差异 0；正式目录只有 1 个 EXE，
  `OneHistoryStudio.Service.*` 为 0。
- 正式 EXE ProductVersion `2.7.3`、FileVersion `2.7.3.0`、SHA-256
  `90711849029D9419439CA05D3339E6ED88D72606B9528EA35429E7117FA10D58`。
- `%AppData%/OneHistoryStudio/bootstrap.json` 保持 `role=server`；正式 UI PID 16808 与
  `--service-host` PID 14496 正常运行，HKCU Run 仍指向正式单 EXE 的 `--service-host`。
- 8738 端口仍监听；匿名 `/api/health` 返回 401，符合鉴权门禁，不能解释为服务未就绪。
- 正式“连接与端口”页可由 UIA 读取 `Server · Ready · http://127.0.0.1:8738/`、server/client 角色控件和
  ServiceHost 状态。尝试补 `ROLE-01` 点击验收时，Windows Graphics Capture 继续报
  `SetIsBorderRequired failed (0x80004002)`，UIA 单选点击通道要求截图状态，故未执行不可靠的盲坐标切换；
  profile 和正式服务器状态均未改变。
- 收口探针另发现：在现有 GUI 会话下新启动 `--exec web.status` / `--exec mcp.status` 可能不自行退出；本次产生的
  PID 10348、4516、3252 已精确清理，只保留正式 UI/ServiceHost。缺陷登记为 V2.7.5 `QM275-31`。
- 2026-07-28 09:33 从最终工作树重新串行执行 Debug/Release 构建，两种配置均为 0 warning / 0 error；
  Wiring、V213、PromptGovernance、V230、V231、V232、ServiceWeb、Docking、GitHubAccount、LanSingleExe
  十套 Smoke 在两种配置下再次全部 PASS。

## 尚待人工

- 未在本次部署中启用 LAN，未修改证书库、URLACL、防火墙或网络类别。
- Private LAN 第二设备配对、真实角色切换、UAC、证书轮换和机器级完整回滚转入质量跟踪并按 36 号文档执行人工验收。
- 继承树显示问题未修改，留待 V2.7.5 质量修复。
