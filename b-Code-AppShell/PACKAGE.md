# OneHistory AppShell

AppShell is a .NET 8 desktop application framework for Windows. It provides a command bus,
WPF shell and docking UI, local services, module hosting, and an MCP gateway.

## Packages

- `OneHistory.AppShell.Core`: framework contracts and command/MCP metadata.
- `OneHistory.AppShell.Services`: settings, logging, SQLite, workspace, modules, and MCP services.
- `OneHistory.AppShell.Shell`: the WPF application shell. Referencing this package brings in Core and Services.
- `OneHistory.AppShell.ServiceHost`: headless WPF service lifecycle, confirmation, and `svc.*` hosting.

## Install

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="2.7.5" />
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="2.7.5" />
```

AppShell 2.7.5 targets .NET 8. The Shell and ServiceHost packages require Windows and WPF. Packages in this
repository-local feed are for OneHistory-owned projects; no public distribution license is granted
by the package itself.

See `b-Office/appshell/二次开发演进手册.md` in the source repository for integration guidance.
