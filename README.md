# Studio Tools Modules

This repository contains three HistoryVulcan modules. Each `z-*` directory is a formal module snapshot discovered by HistoryVulcan; its `module.manifest.json` is the module identity and its artifact paths are release-relative.

| Module | Source | Formal Z snapshot | Domain | UI |
| --- | --- | --- | --- | --- |
| ActiveDock 2.3.3 | `b-Code-ActiveDock` | `z-ActiveDock` | `ActiveDock` | yes |
| GitHubConnection 1.0.2 | `b-Code-GitHubConnection` | `z-GitHubConnection` | `GitHubConnection` | yes |
| StudioTools 1.2.1 | `b-Code-StudioTools` | `z-StudioTools` | `StudioTools` | no |

All modules reference the formal `../2026-023-HistoryVulcan/z-HistoryVulcan/host/HistoryVulcan.Core.dll` contract and do not copy host assemblies into a module snapshot.

## Build And Publish

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Publish-StudioModules.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Publish-StudioModules.ps1 -Publish
```

The first command builds Release assemblies and replaces `b-Publish/current/<module>` candidates after manifest and SHA-256 validation. `-Publish` requires a clean committed source tree, archives the prior Z snapshot under `b-Publish/history/<module>/`, and atomically replaces the matching `z-*` directory. HistoryVulcan discovers these Z snapshots on `module.reload`; no AppData module slot or `tool.scan`/`tool.sync` deployment is used.
