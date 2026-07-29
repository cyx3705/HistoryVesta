# AppShell 3.0.2 模块注册窗口右侧合并整改

> 发现日期：2026-07-29
> 影响版本：3.0.0 / 3.0.1 源码基线
> 修复版本：3.0.2 候选
> 状态：实现、全量门禁与 OHS 消费审核完成；未正式发布、未冻结

## 1. 问题

Shell 建立默认布局时，会把同一 `DockSide` 的窗口合并进一个 `LayoutAnchorablePane`。但模块装载发生在
`DockingHost.Initialize` 之后，运行期 `RegisterWindow` 最终调用 `PlaceAtSide`，旧实现无条件创建新的
`LayoutAnchorablePane`。因此模块以常规 `DockSide.Right` 注册 UI 时，右侧已经存在“指令详情/模块管理”
标签组，框架仍会再切出一块右侧窗格，形成窗口内重复分区。

主窗口缩放还存在另一条比例污染路径：AvalonDock 在重排完成前继续保留旧像素宽度，布局事件可能把这些
瞬时像素值除以新的宿主尺寸并误记成用户拖动比例。OHS 实际复现中，恢复窗口宽度为 `793px`，保存布局却
同时保留左栏 `310.8px`、右栏 `404.04px` 和另一侧栏 `25px`，中央列只剩约 `53px`，其星级宽度还被放大为
`181304288.63654545*`。移动页面、最大化和还原会持续放大这种污染。

## 2. 整改

- 运行期向左、右、上、下侧放置窗口时，先查找同侧现有的可见非浮动窗格。
- 找到同侧窗格后，将新窗口直接加入其标签集合并选中新标签。
- 同侧完全不存在窗格时，才按声明比例创建新窗格。
- `RegisterWindow`、模块 `IShellUiRegistrar.RegisterToolWindow`、窗口复位和 `win.dock` 共用该规则。
- 模块仍声明 `DockSide.Right`，无需知道标准窗口 Id，也无需人工填写 `DefaultTabTarget`。
- 中央文档、显式 `DockSide.Tab`、浮动窗口、owner 回收和保存布局合同不变。
- 主窗口 `SizeChanged` 期间停止布局手势采样；AvalonDock 重排并按既有比例重新施加尺寸后，才重建基线。
- 同一轴上的全部侧栏合计最多占宿主 `80%`，至少为中央工作区保留 `20%`；左右与上下分别计算。
- 拖动分隔条、`win.dock`、`win.ratio`、窗口缩放和布局恢复统一经过比例归一。
- 已保存的超配像素布局在下次加载时自动压回合法范围，不要求消费者删除布局文件。

## 3. 验证

- [x] 新增运行期右侧模块窗口，与“指令详情/模块管理”共用唯一右侧窗格。
- [x] 新模块窗口注册后成为右侧选中标签，`ToolWindowInfo.Side` 仍为 `Right`。
- [x] `ResetWindow` 后仍回到同一右侧窗格，不重新分裂侧栏。
- [x] 超配左右侧栏自动归一并保留至少 20% 中央工作区。
- [x] 主窗口从 1000px 缩到 620px 不会被识别为新的分隔条手势，左右比例保持稳定。
- [x] 保存左右各 480px 的历史坏布局后重新加载，侧栏合计自动恢复到 80% 以内。
- [x] Debug 构建 0 warning / 0 error；Docking 定向测试 22/22、全量测试 74/74 通过。
- [x] Release 构建 0 warning / 0 error；全量测试 74/74 通过。
- [x] `dotnet format --verify-no-changes` 与公开 API 基线检查通过。
- [x] 3.0.2 staging 生成完成：`sourceDirty=true`、漏洞审计无已知漏洞、PackageSmoke 通过，四个程序集均为
  `3.0.2.0`，四个运行包、符号包、4 份消费手册、1 份复用说明和 14 条 SHA-256 记录齐全。
- [x] OHS 2.7.7 从 staging 独立还原 AppShell 3.0.2；Debug/Release 均 0 warning / 0 error，11 套
  Smoke 全部通过，运行时版本投影为 `OHS 2.7.7 / AppShell 3.0.2`。

源码提交后须重新生成 `sourceDirty=false` 的 staging 并复核；正式审核通过前不执行
`Publish-AppShell.ps1 -Version 3.0.2 -Publish`，不更新 Z 级正式快照，也不创建标签。
