# Studio Tools Modules

This repository contains one HistoryVulcan module. Each `z-*` directory is a formal module snapshot discovered by HistoryVulcan; its `module.manifest.json` is the module identity and its artifact paths are release-relative.

| Module | Source | Formal Z snapshot | Domain | UI |
| --- | --- | --- | --- | --- |
| StudioTools 1.2.1 | `b-Code-StudioTools` | `z-StudioTools` | `StudioTools` | no |

ActiveDock has moved to `2026-021-HistoryMercury` and been renamed MercuryDock 3.0.0 (source `b-Code-MercuryDock`, snapshot `z-MercuryDock`, command prefix `dock` unchanged).

GitHubConnection has merged into `2026-020-HistoryJanus` 3.4.0 (page `github` tabbed with `projops`; commands `github.status` / `github.accounts` / `github.test` unchanged). Its source, `z-GitHubConnection` snapshot and publish wiring are removed here; clean up any legacy `GitHubConnection.dll` in host module slots before restart to avoid duplicate pages.

All modules reference the formal `../2026-023-HistoryVulcan/z-HistoryVulcan/host/HistoryVulcan.Core.dll` contract and do not copy host assemblies into a module snapshot.

## Build And Publish

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Publish-StudioModules.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Publish-StudioModules.ps1 -Publish
```

The first command builds Release assemblies and replaces `b-Publish/current/<module>` candidates after manifest and SHA-256 validation. `-Publish` requires a clean committed source tree, archives the prior Z snapshot under `b-Publish/history/<module>/`, and atomically replaces the matching `z-*` directory. HistoryVulcan discovers these Z snapshots on `module.reload`; no AppData module slot or `tool.scan`/`tool.sync` deployment is used.
