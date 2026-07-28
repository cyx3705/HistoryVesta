param(
    [string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot ".."))
$DeliveryRoot = Join-Path $RepoRoot "z-Package-AppShell"

function Get-AppShellVersionProperties {
    $project = Join-Path $ComponentRoot "src\App\App.csproj"
    $output = & dotnet msbuild $project -nologo `
        -getProperty:AppShellVersion -getProperty:FileVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to evaluate the AppShell version source"
    }

    try {
        return (($output -join "`n") | ConvertFrom-Json).Properties
    }
    catch {
        throw "AppShell version evaluation returned invalid JSON: $($output -join ' ')"
    }
}

$VersionProperties = Get-AppShellVersionProperties
$SourceVersion = [string]$VersionProperties.AppShellVersion
$ExpectedFileVersion = [string]$VersionProperties.FileVersion
if ([string]::IsNullOrWhiteSpace($SourceVersion) -or
    [string]::IsNullOrWhiteSpace($ExpectedFileVersion)) {
    throw "AppShellVersion or FileVersion evaluated to an empty value"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $SourceVersion
}
elseif ($Version -ne $SourceVersion) {
    throw "AppShellVersion.props declares $SourceVersion; requested $Version"
}

$StageRoot = [IO.Path]::GetFullPath((Join-Path $DeliveryRoot "staging\$Version"))
if (-not $StageRoot.StartsWith([IO.Path]::GetFullPath($DeliveryRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging path escaped the delivery root: $StageRoot"
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-File {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected file was not produced: $Path"
    }
}

function Publish-ImmutableFile {
    param([string]$Source, [string]$Destination)
    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite immutable release artifact: $Destination"
    }
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $temporary = "$Destination.tmp-$([Guid]::NewGuid().ToString('N'))"
    Copy-Item -LiteralPath $Source -Destination $temporary
    Move-Item -LiteralPath $temporary -Destination $Destination
}

$mutexInput = [Text.Encoding]::UTF8.GetBytes($ComponentRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexHash = (($sha256.ComputeHash($mutexInput) | ForEach-Object { $_.ToString("X2") }) -join "").Substring(0, 16)
}
finally {
    $sha256.Dispose()
}
$publishMutex = [Threading.Mutex]::new($false, "Local\OneHistory.AppShell.Publish.$mutexHash")
$mutexAcquired = $false
try {
    $mutexAcquired = $publishMutex.WaitOne(0)
}
catch [Threading.AbandonedMutexException] {
    $mutexAcquired = $true
}
if (-not $mutexAcquired) {
    $publishMutex.Dispose()
    throw "Another AppShell publish is already running for $ComponentRoot"
}

Push-Location $ComponentRoot
$PreviousNugetPackages = $env:NUGET_PACKAGES
$NugetCache = Join-Path ([IO.Path]::GetTempPath()) ("appshell-release-cache-" + [Guid]::NewGuid().ToString("N"))
$PipelineSucceeded = $false
try {
    if (Test-Path -LiteralPath $StageRoot) {
        & dotnet build-server shutdown | Out-Null
        Remove-Item -LiteralPath $StageRoot -Recurse -Force
    }
    $PackagesDir = Join-Path $StageRoot "packages"
    $DemoDir = Join-Path $StageRoot "demo"
    $BuildArtifacts = Join-Path $StageRoot "artifacts"
    $SmokeArtifacts = Join-Path $StageRoot "package-smoke-artifacts"
    New-Item -ItemType Directory -Force -Path `
        $PackagesDir, $DemoDir, $BuildArtifacts, $SmokeArtifacts, $NugetCache | Out-Null
    $env:NUGET_PACKAGES = $NugetCache

    $sourceStatus = (& git -C $RepoRoot status --porcelain -- b-Code-AppShell) -join "`n"
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
    if ($Publish -and $sourceDirty) {
        throw "Formal publish requires a committed, clean b-Code-AppShell source tree. Run staging without -Publish first."
    }

    $artifactsProperty = "-p:ArtifactsPath=$BuildArtifacts"
    Invoke-Dotnet @("restore", "AppShell.sln", "--locked-mode", $artifactsProperty)
    Invoke-Dotnet @("build", "AppShell.sln", "-c", "Debug", "--no-restore", $artifactsProperty)
    Invoke-Dotnet @("test", "tests\AppShell.Tests\AppShell.Tests.csproj", "-c", "Debug",
        "--no-build", "--no-restore", $artifactsProperty)
    Invoke-Dotnet @("build", "AppShell.sln", "-c", "Release", "--no-restore", $artifactsProperty)
    Invoke-Dotnet @("test", "tests\AppShell.Tests\AppShell.Tests.csproj", "-c", "Release",
        "--no-build", "--no-restore", $artifactsProperty)

    $auditPath = Join-Path $StageRoot "vulnerability-audit.json"
    $auditOutput = & dotnet list AppShell.sln package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet vulnerability audit failed"
    }
    $auditText = $auditOutput -join "`n"
    [IO.File]::WriteAllText($auditPath, $auditText, [Text.UTF8Encoding]::new($false))
    $audit = $auditText | ConvertFrom-Json
    $vulnerabilities = @()
    foreach ($project in $audit.projects) {
        $frameworksProperty = $project.PSObject.Properties["frameworks"]
        if ($null -eq $frameworksProperty) { continue }
        foreach ($framework in $frameworksProperty.Value) {
            foreach ($collectionName in @("topLevelPackages", "transitivePackages")) {
                $collectionProperty = $framework.PSObject.Properties[$collectionName]
                if ($null -eq $collectionProperty) { continue }
                foreach ($package in $collectionProperty.Value) {
                    $packageVulnerabilities = $package.PSObject.Properties["vulnerabilities"]
                    if ($null -ne $packageVulnerabilities) {
                        $vulnerabilities += @($packageVulnerabilities.Value)
                    }
                }
            }
        }
    }
    if ($vulnerabilities.Count -ne 0) {
        throw "NuGet vulnerability audit found $($vulnerabilities.Count) vulnerable dependency entries"
    }

    $packProjects = @(
        "src\AppShell.Core\AppShell.Core.csproj",
        "src\AppShell.Services\AppShell.Services.csproj",
        "src\AppShell.Shell\AppShell.Shell.csproj",
        "src\AppShell.ServiceHost\AppShell.ServiceHost.csproj"
    )
    foreach ($project in $packProjects) {
        Invoke-Dotnet @("pack", $project, "-c", "Release", "--no-build", "--no-restore",
            $artifactsProperty, "-o", $PackagesDir)
    }

    $packageIds = @(
        "OneHistory.AppShell.Core",
        "OneHistory.AppShell.Services",
        "OneHistory.AppShell.Shell",
        "OneHistory.AppShell.ServiceHost"
    )
    foreach ($id in $packageIds) {
        Assert-File (Join-Path $PackagesDir "$id.$Version.nupkg")
        Assert-File (Join-Path $PackagesDir "$id.$Version.snupkg")
    }
    if (Get-ChildItem -LiteralPath $PackagesDir -Filter "AppShell.$Version.nupkg" -ErrorAction SilentlyContinue) {
        throw "The demo WinExe was packed as a NuGet library"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($id in $packageIds) {
        $path = Join-Path $PackagesDir "$id.$Version.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entryNames = @($archive.Entries | ForEach-Object FullName)
            if ($entryNames -notcontains "PACKAGE.md") {
                throw "$id package is missing PACKAGE.md"
            }
            if (-not ($entryNames | Where-Object { $_ -like "lib/*.xml" })) {
                throw "$id package is missing XML documentation"
            }
            $nuspec = $archive.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
            $reader = [IO.StreamReader]::new($nuspec.Open())
            try { $nuspecText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($nuspecText -notmatch "<id>$([Regex]::Escape($id))</id>" -or
                $nuspecText -notmatch "<version>$([Regex]::Escape($Version))</version>") {
                throw "$id nuspec identity mismatch"
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $smokeProject = "tests\PackageSmoke\PackageSmoke.csproj"
    $smokeConfig = Join-Path $StageRoot "PackageSmoke.NuGet.Config"
    $escapedPackagesDir = [Security.SecurityElement]::Escape($PackagesDir)
    $smokeConfigText = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="AppShell staging" value="$escapedPackagesDir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    [IO.File]::WriteAllText($smokeConfig, $smokeConfigText, [Text.UTF8Encoding]::new($false))
    $smokeArtifactsProperty = "-p:ArtifactsPath=$SmokeArtifacts"
    Invoke-Dotnet @("restore", $smokeProject, "--force-evaluate", "--configfile", $smokeConfig,
        "--packages", (Join-Path $StageRoot "smoke-cache"), $smokeArtifactsProperty)
    Invoke-Dotnet @("build", $smokeProject, "-c", "Release", "--no-restore", $smokeArtifactsProperty)
    $runtimeConfigs = @(Get-ChildItem -LiteralPath (Join-Path $SmokeArtifacts "bin\PackageSmoke") `
        -Recurse -Filter "PackageSmoke.runtimeconfig.json" -File)
    if ($runtimeConfigs.Count -ne 1) {
        throw "Expected one PackageSmoke runtimeconfig, found $($runtimeConfigs.Count)"
    }
    $SmokeOutput = $runtimeConfigs[0].Directory.FullName
    $smokeDll = Join-Path $SmokeOutput "PackageSmoke.dll"
    Assert-File $smokeDll
    Invoke-Dotnet @($smokeDll)

    $smokeFiles = @(Get-ChildItem -LiteralPath $SmokeOutput -Recurse -File)
    $smokeBytes = ($smokeFiles | Measure-Object -Property Length -Sum).Sum
    $forbiddenAssets = @($smokeFiles | Where-Object {
        $relative = $_.FullName.Substring($SmokeOutput.Length).TrimStart('\', '/')
        $relative -match '(^|[\\/])runtimes[\\/](linux|osx|ios|android|win-(arm|arm64|x86))'
    })
    if ($forbiddenAssets.Count -ne 0) {
        throw "PackageSmoke contains non-target runtime assets: $($forbiddenAssets.FullName -join ', ')"
    }
    $smokeLimit = 10MB
    if ($smokeBytes -gt $smokeLimit) {
        $largest = $smokeFiles | Sort-Object Length -Descending | Select-Object -First 20
        throw "PackageSmoke output is $smokeBytes bytes, limit is $smokeLimit. Largest files: " +
            (($largest | ForEach-Object { "$($_.Length):$($_.FullName)" }) -join '; ')
    }
    Write-Host "PackageSmoke footprint: $smokeBytes bytes ($($smokeFiles.Count) files), RID win-x64"

    Invoke-Dotnet @("publish", "src\App\App.csproj", "-c", "Release", "--no-restore",
        $artifactsProperty, "--self-contained", "false", "-r", "win-x64", "-o", $DemoDir)
    $demoExe = Join-Path $DemoDir "AppShell.exe"
    Assert-File $demoExe
    $demoVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($demoExe).FileVersion
    if ($demoVersion -ne $ExpectedFileVersion) {
        throw "Demo file version is $demoVersion, expected $ExpectedFileVersion"
    }

    $demoZip = Join-Path $PackagesDir "AppShell-Demo-$Version-win-x64-framework-dependent.zip"
    Compress-Archive -Path (Join-Path $DemoDir "*") -DestinationPath $demoZip -CompressionLevel Optimal

    $artifactFiles = @(Get-ChildItem -LiteralPath $PackagesDir -File | Sort-Object Name)
    $checksumLines = foreach ($file in $artifactFiles) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        "$hash  $($file.Name)"
    }
    $checksumPath = Join-Path $StageRoot "$Version.sha256"
    [IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

    $sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    $manifestArtifacts = foreach ($file in $artifactFiles) {
        [ordered]@{
            file = $file.Name
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "AppShell"
        version = $Version
        channel = $(if ($Publish) { "local" } else { "staging" })
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        sdk = (& dotnet --version).Trim()
        targetFrameworks = @("net8.0", "net8.0-windows")
        demoRid = "win-x64"
        selfContained = $false
        artifacts = @($manifestArtifacts)
    }
    $manifestPath = Join-Path $StageRoot "$Version.json"
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($manifestPath, $manifestJson + "`n", [Text.UTF8Encoding]::new($false))

    if ($Publish) {
        foreach ($file in $artifactFiles) {
            Publish-ImmutableFile $file.FullName (Join-Path $DeliveryRoot "feed\$($file.Name)")
        }
        Publish-ImmutableFile $checksumPath (Join-Path $DeliveryRoot "checksums\$Version.sha256")
        Publish-ImmutableFile $manifestPath (Join-Path $DeliveryRoot "manifest\$Version.json")
        Write-Host "Published AppShell $Version to $DeliveryRoot"
    }
    else {
        Write-Host "Staged AppShell $Version at $StageRoot"
    }
    $PipelineSucceeded = $true
}
finally {
    & dotnet build-server shutdown | Out-Null
    $env:NUGET_PACKAGES = $PreviousNugetPackages
    $workspaceRestoreOutput = & dotnet restore AppShell.sln --locked-mode --force-evaluate 2>&1
    $workspaceRestoreExitCode = $LASTEXITCODE
    if (Test-Path -LiteralPath $NugetCache) {
        Remove-Item -LiteralPath $NugetCache -Recurse -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
    if ($mutexAcquired) {
        $publishMutex.ReleaseMutex()
    }
    $publishMutex.Dispose()
    if ($workspaceRestoreExitCode -ne 0) {
        $message = "Failed to restore the workspace NuGet asset graph after isolated packaging: " +
            ($workspaceRestoreOutput -join " ")
        if ($PipelineSucceeded) {
            throw $message
        }
        Write-Warning $message
    }
}
