# 三维坐标机器人前端设计

这是一个基于 **C# WinForms + .NET 8** 的三维坐标机器人前端项目，目前已经具备可运行的桌面端界面，重点围绕以下能力展开：

- 三维线框视图与路径展示
- 命令行式交互入口
- 路径规划与采样
- 三轴频率换算
- 串口相关工具与诊断
- 仿真演示与数据展示面板

当前代码已经从“概念验证”推进到“可以实际运行和联调”的阶段，README 这里同步为当前开发进度版本。

## 项目现状

当前仓库中已经包含并可运行的主要模块有：

- `Form1` 主窗体
- `CommandParser` 命令解析器
- `PathPlanner` 路径规划核心
- `Wireframe3DViewer` 三维线框显示
- `ConsoleControl` 命令控制台
- `GraphicalPanel` 图形操作面板
- `DataProcessingPanel` 数据处理面板
- `PathDataTablePanel` 路径数据表格面板
- `SimulationDemoPanel` 仿真演示面板
- `SerialPortService`、`SerialProtocol`、`SerialDiagnostics` 等串口相关模块
- `BaudRateScanner`、`SerialAutoBaud` 等辅助工具

项目入口已经支持：

- 正常启动桌面程序
- 通过命令行参数执行串口测试模式：`serial-test`、`serialtest`、`--serial-test`

## 已实现功能

### 1. 主界面与三维展示

- 主窗体集成了左侧三维视图和右侧/下方功能区
- 支持展示控制点、插值点、采样点和当前位置信息
- 用于查看路径规划过程和当前仿真状态

### 2. 命令交互

- 提供命令输入与执行反馈
- 支持从图形面板、数据处理面板、仿真面板触发命令
- 支持示例脚本一键运行

### 3. 路径规划

- 已集成 `PathPlanner`
- 支持控制点管理
- 支持插值与路径采样
- 支持路径数据展示
- 支持频率换算所需的数据准备

### 4. 串口与诊断

- 已有串口服务与协议封装
- 提供串口诊断和辅助测试工具
- 支持命令行串口测试入口

### 5. 仿真与数据面板

- 已有仿真演示面板
- 已有路径数据表格面板
- 已有数据处理面板
- 便于联调路径、显示和串口逻辑

## 技术栈

- `C#`
- `.NET 8`
- `WinForms`
- `System.IO.Ports`

## 启动方式

在 Visual Studio 中直接打开解决方案并运行即可。

如果从命令行启动，可使用：

```bash
dotnet run
```

串口测试模式可通过命令行参数进入：

```bash
dotnet run -- serial-test
```

或：

```bash
dotnet run -- --serial-test
```

## 目录说明

```text
b-Code-三维坐标机器人前端设计/
├─ Form1.cs                    主窗体
├─ Form1.Designer.cs           主窗体布局
├─ Program.cs                  程序入口
├─ CommandParser.cs            命令解析
├─ PathPlanner.cs              路径规划核心
├─ Wireframe3DViewer.cs        三维线框显示
├─ ConsoleControl.cs           命令控制台
├─ GraphicalPanel.cs           图形操作面板
├─ DataProcessingPanel.cs       数据处理面板
├─ PathDataTablePanel.cs       路径数据表格
├─ SimulationDemoPanel.cs      仿真演示面板
├─ SerialPortService.cs        串口服务
├─ SerialProtocol.cs           串口协议
├─ SerialDiagnostics.cs        串口诊断
├─ SerialAutoBaud.cs           自动波特率辅助
├─ BaudRateScanner.cs          波特率扫描
└─ Models/Utils                数据模型与工具
```

## 当前开发进度说明

当前版本的重点是：

1. 把前端界面、命令系统、路径规划和仿真展示串起来
2. 让路径数据在界面上能看、能算、能调
3. 为后续和下位机通信做串口与协议准备

也就是说，这个阶段已经不是空白骨架，而是一个可以继续联调和扩展的功能型前端。

## 后续可继续完善的方向

- 更完整的串口交互流程
- 更细的下位机协议对接
- 路径导出与导入
- 更丰富的仿真细节和状态提示
- 进一步整理命令体系与 UI 交互

