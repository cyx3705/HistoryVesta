using System.Text;
using System.Text.Json;
using AppShell.Core.Commands;

namespace OneHistoryStudio.Mcp;

/// <summary>
/// mcp.* 指令域。V21-M1 先提供 mcp.schema(MC-05,本地验收/调试入口,零网络);
/// V21-M2 追加 mcp.start / mcp.stop / mcp.status(MG-05)。
/// </summary>
public static class McpCommands
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void RegisterAll(
        CommandRegistry registry, Func<CommandBus?> busAccessor,
        Func<McpGateway?> gateway, AppShell.Core.Storage.ISettingsService settings,
        OneHistoryStudio.Git.HistoryRecorder history)
    {
        var exporter = new CommandSchemaExporter(registry)
        {
            DescriptionsProvider = history.AllMcpDescriptions,
        };
        registry.Register(BuildSchema(exporter, registry));
        registry.Register(BuildParse(busAccessor));
        registry.Register(BuildDesc(exporter, history));
        registry.Register(BuildStart(gateway));
        registry.Register(BuildStop(gateway));
        registry.Register(BuildStatus(gateway, settings));
    }

    // ---------------------------------------------------------------- mcp.desc(V2.1.1 提示词管理)

    private static CommandDescriptor BuildDesc(CommandSchemaExporter exporter, OneHistoryStudio.Git.HistoryRecorder history) => new()
    {
        Name = "mcp.desc",
        Summary = "查看/修改 MCP 工具提示词(description):text= 保存覆盖,reset=true 恢复指令自带说明",
        Example = "mcp.desc name=proj.list text=\"列出 OneHistory 项目库全部项目\"",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "指令名或工具名(如 proj.list / proj_list)",
                Required = true,
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "text",
                Description = "新提示词(客户端下次 tools/list 即生效);省略则只查看",
                Position = 1,
            },
            new ParameterSpec
            {
                Name = "reset",
                Description = "true 时清除覆盖,恢复指令自带 Summary",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var name = ctx.RequireString("name").Trim();
            var tool = exporter.Find(name);
            if (tool == null)
                return CommandResult.Fail($"未找到指令/工具: {name}(硬排除的 mcp.*/debug.*/app.exit 无 MCP 形态)");

            if (ctx.GetBool("reset"))
            {
                history.DeleteMcpDescription(tool.CommandName);
                return CommandResult.Ok($"已恢复默认提示词: {tool.CommandName}\n{tool.DefaultDescription}");
            }

            var text = ctx.GetString("text");
            if (string.IsNullOrWhiteSpace(text))
            {
                var sb = new StringBuilder();
                sb.Append($"{tool.ToolName} ← {tool.CommandName}{(tool.Dangerous ? "  ⚠危险(MCP 拒绝执行)" : "")}");
                sb.Append($"\n当前生效: {tool.Description}");
                if (tool.Customized)
                    sb.Append($"\n指令自带: {tool.DefaultDescription}\n(mcp.desc name={tool.CommandName} reset=true 可恢复)");
                return CommandResult.Ok(sb.ToString(), tool);
            }

            text = text.Trim();
            if (text.Length > 2000)
                return CommandResult.Fail($"提示词过长({text.Length} 字符,上限 2000)");

            history.SetMcpDescription(tool.CommandName, text);
            return CommandResult.Ok($"已保存提示词覆盖: {tool.CommandName}\n{text}\n(客户端下次 tools/list 即生效)");
        }),
    };

    // ---------------------------------------------------------------- mcp.start / stop / status(MG-05)

    private static CommandDescriptor BuildStart(Func<McpGateway?> gateway) => new()
    {
        Name = "mcp.start",
        Summary = "启动 MCP 服务(仅 127.0.0.1;策略/令牌经 app.set mcp.policy / mcp.token 配置)",
        Example = "mcp.start port=8737",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "port",
                Description = "监听端口(缺省读 mcp.port 配置,默认 8737;显式指定时持久化)",
                Type = ParamType.Int,
                Position = 0,
            },
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var g = gateway();
            if (g == null)
                return CommandResult.Fail("网关未装配");
            int? port = ctx.Has("port") ? ctx.GetInt("port") : null;
            var (success, message) = g.Start(port);
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        }),
    };

    private static CommandDescriptor BuildStop(Func<McpGateway?> gateway) => new()
    {
        Name = "mcp.stop",
        Summary = "停止 MCP 服务并释放端口",
        Example = "mcp.stop",
        Handler = CommandDescriptor.Sync(_ =>
        {
            var g = gateway();
            if (g == null)
                return CommandResult.Fail("网关未装配");
            var (success, message) = g.Stop();
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        }),
    };

    private static CommandDescriptor BuildStatus(
        Func<McpGateway?> gateway, AppShell.Core.Storage.ISettingsService settings) => new()
    {
        Name = "mcp.status",
        Summary = "查看 MCP 服务状态(运行/端口/策略/暴露工具数/累计调用/最近一次调用)",
        Example = "mcp.status",
        Handler = CommandDescriptor.Sync(_ =>
        {
            var g = gateway();
            if (g == null)
                return CommandResult.Fail("网关未装配");

            var sb = new StringBuilder();
            sb.Append($"MCP 服务: {(g.IsRunning ? $"运行中 http://127.0.0.1:{g.Port}/mcp" : "未启动(mcp.start 开启)")}");
            sb.Append($"\n  策略   : {g.Policy}(app.set key=mcp.policy value=readonly|standard)");
            sb.Append($"\n  暴露   : {g.VisibleTools().Count} 个工具(mcp.schema 看全量形态)");
            sb.Append($"\n  令牌   : {(string.IsNullOrEmpty(settings.Get(McpGateway.KeyToken)) ? "未设置(本机回环可信)" : "已设置(Bearer 必需)")}");
            sb.Append($"\n  自启动 : mcp.autostart = {settings.Get(McpGateway.KeyAutostart) ?? "false"}");
            sb.Append($"\n  调用   : 累计 {g.CallCount} 次,最近 {g.LastCall}");
            return CommandResult.Ok(sb.ToString());
        }),
    };

    // ---------------------------------------------------------------- mcp.schema(MC-05)

    private static CommandDescriptor BuildSchema(CommandSchemaExporter exporter, CommandRegistry registry) => new()
    {
        Name = "mcp.schema",
        Summary = "查看指令的 MCP 工具形态(不带参列全部;带 name 输出单条完整 JSON Schema)",
        Example = "mcp.schema name=proj.create",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "指令名或工具名(如 proj.create / proj_create);省略列出全部",
                Position = 0,
            },
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var name = ctx.GetString("name");

            if (!string.IsNullOrWhiteSpace(name))
            {
                var tool = exporter.Find(name.Trim());
                if (tool == null)
                    return CommandResult.Fail($"未找到指令/工具: {name}(或被硬排除,见 mcp.schema 全量清单)");

                var json = JsonSerializer.Serialize(new
                {
                    name = tool.ToolName,
                    command = tool.CommandName,
                    dangerous = tool.Dangerous,
                    description = tool.Description,
                    inputSchema = tool.InputSchema,
                }, Pretty);
                return CommandResult.Ok(json, tool);
            }

            var tools = exporter.ExportTools();
            var total = registry.All().Count;
            var sb = new StringBuilder();
            sb.Append($"MCP 工具清单: {tools.Count} 个(注册表 {total} 条指令,硬排除 {total - tools.Count} 条):");
            foreach (var t in tools)
            {
                var paramCount = ((System.Text.Json.Nodes.JsonObject?)t.InputSchema["properties"])?.Count ?? 0;
                sb.Append($"\n  {t.ToolName,-28} ← {t.CommandName}");
                if (paramCount > 0)
                    sb.Append($"  [{paramCount} 参数]");
                if (t.Dangerous)
                    sb.Append("  ⚠危险(MCP 拒绝执行)");
            }

            sb.Append("\nmcp.schema name=<指令名> 查看单条完整 JSON Schema");
            return CommandResult.Ok(sb.ToString(), tools);
        }),
    };

    // ---------------------------------------------------------------- mcp.parse(MC-06 验收入口)

    private static CommandDescriptor BuildParse(Func<CommandBus?> busAccessor) => new()
    {
        Name = "mcp.parse",
        Summary = "调试:模拟 tools/call 反向解析——JSON arguments 组装为指令文本,exec=true 随即经总线执行",
        Example = "mcp.parse command=proj.list args=\"{\\\"filter\\\":\\\"2026\\\"}\" exec=true",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "command",
                Description = "目标指令名",
                Required = true,
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "args",
                Description = "JSON 对象文本(工具调用的 arguments)",
                Position = 1,
            },
            new ParameterSpec
            {
                Name = "argsfile",
                Description = "从文件读 JSON(替代 args,规避命令行转义;自动化验收用)",
            },
            new ParameterSpec
            {
                Name = "exec",
                Description = "true 时组装后立即经总线执行(来源 MCP:parse)",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        Handler = async ctx =>
        {
            var command = ctx.RequireString("command").Trim();
            var argsText = ctx.GetString("args");
            var argsFile = ctx.GetString("argsfile");
            if (string.IsNullOrWhiteSpace(argsText) && !string.IsNullOrWhiteSpace(argsFile))
            {
                if (!System.IO.File.Exists(argsFile))
                    return CommandResult.Fail($"argsfile 不存在: {argsFile}");
                argsText = await System.IO.File.ReadAllTextAsync(argsFile);
            }

            JsonElement args;
            try
            {
                args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsText) ? "{}" : argsText)
                    .RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"args 不是合法 JSON: {ex.Message}");
            }

            var text = CommandSchemaExporter.BuildCommandText(command, args);
            if (!ctx.GetBool("exec"))
                return CommandResult.Ok($"组装结果: {text}", text);

            var bus = busAccessor();
            if (bus == null)
                return CommandResult.Fail("总线未就绪");

            // 与网关 tools/call 同轨:组装文本经总线执行,回显/留痕走既有通道(铁律 2)
            var result = await bus.ExecuteAsync(text, "MCP:parse");
            return result.Success
                ? CommandResult.Ok($"组装结果: {text}\n执行成功(结果见上一条回显)", text)
                : CommandResult.Fail($"组装结果: {text}\n执行失败(详见上一条回显)");
        },
    };
}
