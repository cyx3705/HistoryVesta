# AppShell 0.7.2 正式部署证据

> 部署时间：2026-07-27 23:28:24 至 23:39:26 +08:00  
> 产品版本：OneHistoryStudio 2.7.0（维护部署，不升级产品版本）  
> 框架版本：AppShell 0.7.2  
> 模块版本：SE2SW 2.2.0

## 部署位置

- 正式 OHS：`C:\OneHistory\OneHistory-Push\OneHistoryStudio`
- OHS 回滚：`C:\OneHistory\OneHistory-Push\OneHistoryStudio-rollback-2.7.0-appshell-0.7.2-20260727-232824`
- 正式 SE2SW：`C:\Users\Administrator\AppData\Roaming\OneHistoryStudio\Modules\SE2SW`
- SE2SW 回滚：`C:\OneHistory\OneHistory-Push\SE2SW-rollback-2.2.0-appshell-0.7.1-20260727-232824`
- 数据库备份：`C:\OneHistory\OneHistory-Push\OneHistoryStudio-DatabaseBackups\main-db-predeploy-2.7.0-appshell-0.7.2-20260727-232824.db`

数据库备份大小为 122880 字节，SHA-256：
`2C598AD6D1B856A2DB3321E07327D03384BAABBE67CAB184D558F3A1CD854F80`。

## 发布物校验

- 全新 staging：`stage/deploy-appshell-0.7.2-20260727-232824`
- 正式 OHS 与 staging：52 / 52 个文件，逐文件 SHA-256 差异 0。
- 正式 SE2SW 与 staging：8 / 8 个文件，逐文件 SHA-256 差异 0。
- `OneHistoryStudio.exe`：ProductVersion 2.7.0，FileVersion 2.7.0.0。
- `OneHistoryStudio.Service.exe`：ProductVersion 2.7.0，FileVersion 2.7.0.0。
- AppShell Core、Services、Shell、ServiceHost：AssemblyVersion 均为 0.7.2.0。
- 发布目录包含六份同源产品文档；命令手册为 113 条核心命令，SHA-256：
  `F1BD4A7A6C204F133026566530D95708E0CF3B3E58A250B1F085586BC7C19995`。

关键正式文件 SHA-256：

| 文件 | SHA-256 |
|---|---|
| `OneHistoryStudio.exe` | `EAF7C4C497FA8D38F694DBF8416C913D8842951B970E83BD3F997FA97EB74CFC` |
| `OneHistoryStudio.Service.exe` | `67EA69B42076DBD10D0CE1EDC47D7D912ECE764B44F3492849BC4363D4A89FAB` |
| `AppShell.Shell.dll` | `66204518CA0382F95669D6383B7014C8338FD5275D264BE089E10A0DF5171F78` |
| `SE2SW.dll` | `CCF928D07E8D2A609D6322F5FCF4AA9F3CC2245EE7658A7816FB03F3210A74E9` |

## 验收结果

- OHS Release 八套 Smoke 全部通过；SE2SW Release Smoke 通过。
- 正式健康端点以本机 Shell 会话访问返回 `status=ok`；前端与服务进程均来自正式目录。
- 运行时共 126 条命令，其中核心 113 条、动态模块 13 条；`page.*` 为 0 条。
- `module.list` 装载 5 个模块；SE2SW 2.2.0 为 `ui:true`，注册 2 条模块命令。
- `win.list` 存在 `se2sw`，owner 为 `SE2SW`，类型为普通停靠工具窗口。
- `win.max name=se2sw` 返回“se2sw 已最大化”；`win.restore` 返回“已恢复原布局”。
- Windows UI Automation 树显示 SE2SW 带窗口位置、自动隐藏和隐藏控制；未出现中央工作页标签。
- 启动后 59 条部署时段日志中 Warn / Error / Fatal 为 0。

Windows Graphics Capture 截图仍返回已知错误
`SetIsBorderRequired failed: 不支持此接口 (0x80004002)`；本次不把截图记为通过，窗口结构由
可访问性树、运行时命令与 Docking Smoke 三条独立证据覆盖。

## 回滚

关闭正式前端并通过 `svc.stop` 停止服务后，将正式 OHS 与 SE2SW 目录移开，再分别把上述两个
回滚目录恢复为原路径。只有在数据库迁移或数据兼容性异常时，才恢复本次升级前的 `main.db`；
本次 AppShell 维护部署没有新增数据库迁移。
