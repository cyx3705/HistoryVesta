# OneHistoryStudio 源码

本目录拥有 OHS 业务源码和模块发布管道。V3 正式交付工程是 `Module/OneHistoryStudio.Module.csproj`；`Studio.csproj` 只保留迁移回归所需的旧应用编译面，不进入正式包。

## 权威入口

- `StudioVersion.props`：唯一 OHS 版本源。
- `Module/module.manifest.json`：模块装载 manifest，版本必须与版本源一致。
- `Module/OneHistoryStudioUiModule.cs`：宿主上下文、命令与五个页面的组合入口。
- `StudioBusinessComposition.cs`：项目、Git 规则、历史和诊断命令组合。
- `eng/Publish-Studio.ps1`：候选验证与正式提升。
- `eng/Deploy-Studio.ps1`：AppShell UI/Service 双槽部署。

## 发布边界

`b-Publish/candidate` 保存当前模块候选，`b-Publish/history` 只保存被替换的正式包，`z-Package-OneHistoryStudio` 只保存最新正式消费快照。正式包不得包含 EXE、PDB、AppShell DLL、deps/runtimeconfig 或旧综合 Help。

```powershell
.\b-Code-Studio\eng\Publish-Studio.ps1
.\b-Code-Studio\eng\Publish-Studio.ps1 -Publish
.\b-Code-Studio\eng\Deploy-Studio.ps1 -Apply
```

发布和部署不启动 AppShell，不修改或测试开机自启动。
