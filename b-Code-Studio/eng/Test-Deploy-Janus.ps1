param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string[]]$Suite,
    [string]$TestFilter
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Publish-Transaction.ps1')

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot '..'))
$PublishRoot = Join-Path $RepoRoot 'b-Publish'
$CandidateRoot = Join-Path $PublishRoot 'candidate'
$WorkRoot = Join-Path $PublishRoot 'work'
$QuarantineRoot = Join-Path $PublishRoot 'quarantine'
$transactionId = [Guid]::NewGuid().ToString('N')
$candidateNew = Join-Path $WorkRoot "candidate-dev-$transactionId"
$candidateBackup = Join-Path $WorkRoot "candidate-previous-$transactionId"
$candidateFailed = Join-Path $QuarantineRoot "candidate-failed-$transactionId"

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

Push-Location $RepoRoot
try {
    foreach ($path in @($WorkRoot, $QuarantineRoot)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }

    Invoke-Dotnet @('restore', 'HistoryJanus.sln', '--locked-mode', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'HistoryJanus.sln', '-c', 'Debug', '--no-restore', '-p:NuGetAudit=false')

    $contractArgs = @('test', 'b-Code-Verify\Contracts\Contracts.csproj', '-c', 'Debug', '--no-build', '--no-restore', '-p:NuGetAudit=false')
    if (-not [string]::IsNullOrWhiteSpace($TestFilter)) { $contractArgs += @('--filter', $TestFilter) }
    Invoke-Dotnet $contractArgs

    $selectedSuites = @($Suite | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
    if ($selectedSuites.Count -eq 0) { throw 'At least one targeted Smoke suite is required' }
    foreach ($suiteName in $selectedSuites) {
        Invoke-Dotnet @('run', '--project', 'b-Code-Verify\Smoke\Smoke.csproj', '-c', 'Debug', '--no-build', '--no-restore', '--', '--suite', $suiteName)
    }

    $moduleOutput = Join-Path $ComponentRoot 'Module\bin\Debug\net8.0-windows'
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj', '-c', 'Debug', '--no-build', '--no-restore', '--', $moduleOutput)

    New-Item -ItemType Directory -Force -Path (Join-Path $candidateNew 'package') | Out-Null
    Copy-Item -LiteralPath (Join-Path $moduleOutput 'HistoryJanus.dll') -Destination $candidateNew
    Copy-Item -LiteralPath (Join-Path $moduleOutput 'HistoryJanus.xml') -Destination $candidateNew
    Copy-Item -LiteralPath (Join-Path $ComponentRoot 'Module\module.manifest.json') -Destination $candidateNew
    $apiDocuments = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'b-Office\package') -Filter '*.md' -File)
    if ($apiDocuments.Count -ne 1) { throw 'b-Office/package must contain exactly one API Markdown document' }
    $apiName = $apiDocuments[0].Name
    Copy-Item -LiteralPath $apiDocuments[0].FullName -Destination (Join-Path $candidateNew ('package\' + $apiName))

    $version = [string](([IO.File]::ReadAllText((Join-Path $candidateNew 'module.manifest.json')) | ConvertFrom-Json).version)
    $manifest = [IO.File]::ReadAllText((Join-Path $candidateNew 'module.manifest.json')) | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName channel -NotePropertyValue 'development' -Force
    [IO.File]::WriteAllText(
        (Join-Path $candidateNew 'module.manifest.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $relativeFiles = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json', "package/$apiName")
    $lines = foreach ($relative in $relativeFiles) {
        $path = Join-Path $candidateNew $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        "$(Get-FileHash -LiteralPath $path -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relative"
    }
    [IO.File]::WriteAllLines((Join-Path $candidateNew 'SHA256SUMS'), $lines, [Text.UTF8Encoding]::new($false))
    Assert-ModulePackage $candidateNew $version 'development'

    $validate = { param($Root) Assert-ModulePackage $Root $version 'development' }
    Invoke-DirectoryPromotion $candidateNew $CandidateRoot $candidateBackup $candidateFailed $validate
    if (Test-Path -LiteralPath $candidateBackup) { Remove-Item -LiteralPath $candidateBackup -Recurse -Force }
    Write-Host "Development-tested module candidate: $CandidateRoot"
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    if (Test-Path -LiteralPath $WorkRoot) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force }
}
