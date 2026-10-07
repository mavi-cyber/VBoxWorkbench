<#
.SYNOPSIS
    Builds the release executables of VBox Workbench into .\dist and writes their SHA-256 checksums.

.DESCRIPTION
    Produces two single-file Windows x64 executables:
      VBoxWorkbench-<version>-win-x64.exe            self-contained, runs without installing .NET
      VBoxWorkbench-<version>-win-x64-needs-dotnet.exe   small, needs the .NET 10 Desktop Runtime
    and SHA256SUMS.txt listing both.

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -SkipTests
#>
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\VBoxWorkbench.App\VBoxWorkbench.App.csproj'
$dist = Join-Path $root 'dist'

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> found in $project" }

if (-not $SkipTests) {
    dotnet test $root -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no release was built.' }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

function Publish([bool]$selfContained, [string]$name) {
    $out = Join-Path $dist '_publish'
    $publishArgs = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--nologo', '-v', 'q', '-o', $out,
        "--self-contained=$($selfContained.ToString().ToLower())",
        '-p:PublishSingleFile=true', '-p:DebugType=none'
    )
    if ($selfContained) {
        # WPF ships native libraries; these two switches fold them into the one .exe and compress it.
        $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true'
    }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name" }
    Move-Item (Join-Path $out 'VBoxWorkbench.App.exe') (Join-Path $dist $name)
    Remove-Item $out -Recurse -Force
}

Publish $true  "VBoxWorkbench-$version-win-x64.exe"
Publish $false "VBoxWorkbench-$version-win-x64-needs-dotnet.exe"

$lines = Get-ChildItem $dist -Filter *.exe | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
# LF line endings and no BOM, so "sha256sum -c SHA256SUMS.txt" works on Linux and macOS too.
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"))

Get-ChildItem $dist | ForEach-Object { '{0,12:N0} bytes  {1}' -f $_.Length, $_.Name }
$lines
