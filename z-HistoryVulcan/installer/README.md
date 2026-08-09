# HistoryVulcan 3.3.1 installer package

Built from formal host snapshot `z-HistoryVulcan` (win-x64, framework-dependent).
Delivered under `z-HistoryVulcan/installer/`.

## Artifacts

| File | Purpose |
| --- | --- |
| `HistoryVulcan-3.3.1-Setup.exe` | Windows installer (Program Files, Start Menu, optional desktop shortcut) |
| `HistoryVulcan-3.3.1-win-x64.7z` | Portable snapshot (host/docs layout without this installer folder) |

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime (framework-dependent host)

## Install

1. Run `HistoryVulcan-3.3.1-Setup.exe` as administrator (or accept UAC).
2. Launch HistoryVulcan from the Start Menu, or run `{install}\host\HistoryVulcan.exe`.

## Portable

Extract `HistoryVulcan-3.3.1-win-x64.7z` and run `host\HistoryVulcan.exe`.

## Integrity

See `SHA256SUMS` in this directory.