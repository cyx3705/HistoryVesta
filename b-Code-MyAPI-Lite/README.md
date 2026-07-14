# MyAPI Lite

极简 DLL 托管机：把 DLL 放进 `Modules` 目录，自动热重载，同时托管为 **HTTP 接口** 和 **MCP 工具**。

零 NuGet 依赖，4 个源码文件（Program / ModuleHost / Invoker / Mcp），net8.0。

> **❄️ 代码冻结声明**
> 本项目作为基础设施已于 2026-07-07 定稿冻结，此后不再修改任何代码。
> 一切功能扩展都通过编写模块 DLL 放入 `Modules` 目录实现，宿主本身永不改动。

---

## 一、部署与启动

```
dotnet publish MyApiLite -c Release
```

把发布产物放到任意目录，直接运行：

```
MyApiLite.exe                                    # 默认端口 5100，模块目录 = exe 旁边的 Modules\
MyApiLite.exe --Port 8080 --ModulesDir D:\Mods   # 自定义端口和模块目录
```

启动后：

- HTTP 服务： `http://<主机>:5100`
- MCP 端点：  `http://<主机>:5100/mcp` （Streamable HTTP，无状态）
- 模块目录不存在会自动创建

本仓库无任何 NuGet 包依赖，`nuget.config` 已清空源，可完全离线构建。

## 二、模块的部署（核心用法）

把模块 DLL 复制进 `Modules\` 目录即可，**宿主无需重启**：

| 操作 | 效果 |
|---|---|
| 放入 / 覆盖 DLL | 约 1 秒后自动整体热重载，接口和 MCP 工具即时上线 / 更新 |
| 删除 DLL | 对应接口和工具自动下线 |
| 放入同名 `.xml` 文档 | 方法注释成为接口描述和 MCP 工具提示词 |

需要一起放入的文件：

1. `你的模块.dll` —— 必须
2. `BaseVariable.dll` —— 模块的契约基类库，必须
3. `你的模块.xml` —— 编译器生成的注释文档，强烈建议（MCP 工具描述的来源）
4. 模块引用的其他第三方 DLL —— 按需

技术要点：DLL 从内存流加载不锁文件，可直接覆盖；旧模块通过可回收 `AssemblyLoadContext` 卸载；文件事件有 800ms 防抖，拷贝多个文件只触发一次重载。

## 三、编写一个模块

新建 net8.0 类库，引用 `BaseVariable.csproj`（或直接引用 `BaseVariable.dll`），csproj 中打开 XML 文档：

```xml
<PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);1591</NoWarn>
</PropertyGroup>
```

模块内必须包含一个 `ModuleInfoBase` 公共子类（没有它的 DLL 视为纯依赖库，只加载不暴露）：

```csharp
using BaseVariable;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "DemoModule";
    public override string Description => "示例模块";
    public override bool Open => false;                      // false=只暴露 MainClassType；true=暴露程序集内全部公共类
    public override Type? MainClassType => typeof(Calculator);
    // 可选：Author / Version / Enabled（false 可整体停用模块）
}
```

业务类的公共方法自动成为接口，**XML 注释即 MCP 工具提示词**：

```csharp
public class Calculator
{
    /// <summary>两个整数相加，返回和</summary>
    /// <param name="a">第一个加数</param>
    /// <param name="b">第二个加数</param>
    public int Add(int a, int b) => a + b;

    /// <summary>支持 async Task&lt;T&gt;，自动等待解包</summary>
    public async Task<string> SayHello(string name = "World") { ... }

    /// <summary>静态方法直接调用，无需实例</summary>
    public static object Now() => new { time = DateTime.Now };
}
```

规则：实例方法按类维度单例调用（每次热重载后重建）；带默认值的参数可省略；返回对象自动序列化为 JSON。
模块识别按基类全名（`BaseVariable.ModuleInfoBase`）鸭子类型匹配，与 BaseVariable.dll 的编译版本无关，旧版编译的模块同样兼容。

## 四、HTTP 接口

| 路由 | 说明 |
|---|---|
| `GET /` | 宿主信息与用法 |
| `GET /api/meta/modules` | 已加载模块列表 |
| `GET /api/meta/endpoints` | 全部接口（含描述、对应 MCP 工具名） |
| `POST /api/meta/reload` | 手动触发重载 |
| `GET /api/{命名空间}/{类}/{方法}?参数=值` | query 传参调用 |
| `POST /api/{命名空间}/{类}/{方法}` | JSON body 传参调用（缺的字段回落到 query） |

示例：

```
curl "http://localhost:5100/api/DemoModule/Calculator/Add?a=1&b=2"        → 3
curl -X POST http://localhost:5100/api/DemoModule/Calculator/Add \
     -H "Content-Type: application/json" -d '{"a":100,"b":23}'            → 123
```

## 五、MCP 服务

宿主内置无状态 MCP 服务端（Streamable HTTP 传输，协议版本 2025-06-18 / 2025-03-26 / 2024-11-05）。
每个动态接口即一个 MCP 工具，工具名为 `命名空间_类_方法`（非法字符替换为下划线）：

- 工具描述 = 方法的 `<summary>` 注释
- 参数 schema = 方法签名（类型映射 + required 判定）+ `<param>` 注释
- DLL 热重载后工具集实时变化，客户端重新 `tools/list` 即可

### 接入 Claude Code

```
claude mcp add --transport http myapi http://localhost:5100/mcp
```

### 接入其他 MCP 客户端（JSON 配置示例）

```json
{
  "mcpServers": {
    "myapi": { "type": "http", "url": "http://localhost:5100/mcp" }
  }
}
```

### 手动调试

```
curl -X POST http://localhost:5100/mcp -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

curl -X POST http://localhost:5100/mcp -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"DemoModule_Calculator_Add","arguments":{"a":1,"b":2}}}'
```

## 六、安全边界

宿主监听 `0.0.0.0` 且**无鉴权**，模块方法通过反射直接执行——只适合部署在可信局域网 / 本机。
如需对外暴露，请在前面加反向代理做鉴权，并把 `Open` 设为 `false` 用 `MainClassType` 精准控制暴露面。

## 七、目录结构

```
MyAPI-Lite/
├── BaseVariable/        模块契约（ModuleInfoBase），模块项目引用它
├── MyApiLite/           宿主本体
│   ├── Program.cs       启动、路由、HTTP 动态调用
│   ├── ModuleHost.cs    模块目录监听、ALC 热加载、端点注册、XML 注释解析
│   ├── Invoker.cs       参数绑定（query / JSON）与反射调用
│   └── Mcp.cs           MCP 服务端（JSON-RPC 2.0，手写零依赖）
├── DemoModule/          示例模块（含 XML 注释规范写法）
└── nuget.config         清空 NuGet 源，离线构建
```
