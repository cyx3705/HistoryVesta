[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryDiana 自身的合同校验入口。
#
# 规则本体不在这里。Diana 作为体系的发布管线拥有者，同时也按模块规则约束自己，
# 因此转发到共用的模块入口。本文件只负责定位仓库根，不得复制或改写任何规则——
# 需要项目差异时改 project.manifest.json 的 contract 节。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'OneHistory.ModuleContract.ps1') `
    -ProjectRoot (Join-Path $PSScriptRoot '..') `
    -Instantiation:$Instantiation

exit $LASTEXITCODE
