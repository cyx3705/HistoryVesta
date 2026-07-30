param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$Suite,
    [string]$TestFilter
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Publish-Transaction.ps1")

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot ".."))
$PublishRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "b-Publish"))
$DeliveryRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "candidate"))
$WorkRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "work"))
$QuarantineRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "quarantine"))
$transactionId = [Guid]::NewGuid().ToString("N")
$BuildRoot = Join-Path $WorkRoot "dev-$transactionId"
$AppRoot = Join-Path $BuildRoot "app"
$temporaryDelivery = Join-Path $WorkRoot "candidate-new-$transactionId"
$backupDelivery = Join-Path $WorkRoot "candidate-previous-$transactionId"
$quarantineDelivery = Join-Path $QuarantineRoot "candidate-failed-$transactionId"
$succeeded = $false

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-UnderRoot {
    param([string]$Path, [string]$Root, [string]$Name)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escaped its root: $fullPath"
    }
    return $fullPath
}

$BuildRoot = Assert-UnderRoot $BuildRoot $WorkRoot "development build"
$temporaryDelivery = Assert-UnderRoot $temporaryDelivery $WorkRoot "temporary development delivery"
$backupDelivery = Assert-UnderRoot $backupDelivery $WorkRoot "development rollback"
$quarantineDelivery = Assert-UnderRoot $quarantineDelivery $QuarantineRoot "failed development delivery"

$mutexInput = [Text.Encoding]::UTF8.GetBytes($ComponentRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexHash = (($sha256.ComputeHash($mutexInput) | ForEach-Object { $_.ToString("X2") }) -join "").Substring(0, 16)
}
finally {
    $sha256.Dispose()
}
$mutex = [Threading.Mutex]::new($false, "Local\OneHistoryStudio.Publish.$mutexHash")
$mutexAcquired = $false
try {
    $mutexAcquired = $mutex.WaitOne(0)
}
catch [Threading.AbandonedMutexException] {
    $mutexAcquired = $true
}
if (-not $mutexAcquired) {
    $mutex.Dispose()
    throw "Another OHS build or publish is already running for $ComponentRoot"
}

Push-Location $RepoRoot
try {
    foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
        if (Test-Path -LiteralPath $transientRoot) {
            Remove-Item -LiteralPath $transientRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $transientRoot | Out-Null
    }

    Invoke-Dotnet @( "restore", "OHS.sln", "--locked-mode", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "build", "OHS.sln", "-c", "Debug", "--no-restore", "-p:NuGetAudit=false" )

    $contractArguments = @( "test", "b-Code-Verify\Contracts\Contracts.csproj", "-c", "Debug",
        "--no-build", "--no-restore", "-p:NuGetAudit=false" )
    if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $contractArguments += @( "--filter", $TestFilter )
    }
    Invoke-Dotnet $contractArguments

    $selectedSuites = @($Suite | ForEach-Object { $_.Trim() } | Where-Object { $_ } |
        Select-Object -Unique)
    if ($selectedSuites.Count -eq 0) {
        throw "At least one targeted Smoke suite is required"
    }
    foreach ($suiteName in $selectedSuites) {
        Invoke-Dotnet @( "run", "--project", "b-Code-Verify\Smoke\Smoke.csproj", "-c", "Debug",
            "--no-build", "--no-restore", "--", "--suite", $suiteName )
    }

    New-Item -ItemType Directory -Force -Path $AppRoot | Out-Null
    Invoke-Dotnet @( "publish", "b-Code-Studio\Studio.csproj", "-c", "Debug", "-r", "win-x64",
        "--self-contained", "false", "-o", $AppRoot, "--no-restore" )
    $exe = Join-Path $AppRoot "OneHistoryStudio.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Debug test deployment did not produce OneHistoryStudio.exe"
    }

    $sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    $sourceStatus = (& git -C $RepoRoot status --porcelain -- b-Code-Studio b-Code-Verify b-Office) -join "`n"
    $report = [ordered]@{
        schemaVersion = 1
        product = "OneHistoryStudio"
        channel = "development"
        configuration = "Debug"
        sourceCommit = $sourceCommit
        sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        contractsFilter = if ([string]::IsNullOrWhiteSpace($TestFilter)) { $null } else { $TestFilter }
        smokeSuites = @($selectedSuites)
    }
    [IO.File]::WriteAllText(
        (Join-Path $AppRoot "development-verification.json"),
        (($report | ConvertTo-Json -Depth 5) + "`n"),
        [Text.UTF8Encoding]::new($false))

    Copy-Item -LiteralPath $AppRoot -Destination $temporaryDelivery -Recurse
    $validate = {
        param($Root)
        if (-not (Test-Path -LiteralPath (Join-Path $Root "OneHistoryStudio.exe") -PathType Leaf)) {
            throw "Development delivery is missing OneHistoryStudio.exe"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $Root "development-verification.json") -PathType Leaf)) {
            throw "Development delivery is missing its verification report"
        }
    }
    Invoke-DirectoryPromotion `
        $temporaryDelivery $DeliveryRoot $backupDelivery $quarantineDelivery $validate
    if (Test-Path -LiteralPath $backupDelivery) {
        Remove-Item -LiteralPath $backupDelivery -Recurse -Force
    }
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    Write-Host "Development-tested candidate is ready at $DeliveryRoot"
    $succeeded = $true
}
catch {
    $failure = $_
    if ((Test-Path -LiteralPath $BuildRoot) -and (-not (Test-Path -LiteralPath $quarantineDelivery))) {
        Move-Item -LiteralPath $BuildRoot -Destination $quarantineDelivery
    }
    throw $failure
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    if ($succeeded) {
        foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
            if (Test-Path -LiteralPath $transientRoot) {
                Remove-Item -LiteralPath $transientRoot -Recurse -Force
            }
        }
    }
    if ($mutexAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
