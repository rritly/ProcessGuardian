param(
    [switch]$FrameworkDependent,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Csproj = Join-Path $RepoRoot 'src\ProcessGuardian.Wpf\ProcessGuardian.Wpf.csproj'
$PublishRoot = Join-Path $RepoRoot 'publish'
$FDOut = Join-Path $PublishRoot 'framework-dependent'
$SCOut = Join-Path $PublishRoot 'self-contained'

function Invoke-PublishProfile {
    param(
        [string]$Name,
        [string]$OutputDir,
        [string[]]$PublishArgs
    )

    Write-Host "==> Publishing profile: $Name"
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    dotnet publish $Csproj -c Release -r win-x64 -o $OutputDir @PublishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for profile '$Name' (exit code $LASTEXITCODE)"
    }

    $exe = Join-Path $OutputDir 'ProcessGuardian.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Expected executable not found: $exe"
    }

    $sizeMB = [Math]::Round(((Get-ChildItem -Recurse -File $OutputDir | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Host "    exe: $exe"
    Write-Host "    output size: $sizeMB MB"
}

if (-not ($FrameworkDependent -or $SelfContained)) {
    $FrameworkDependent = $true
    $SelfContained = $true
}

if ($FrameworkDependent) {
    Invoke-PublishProfile -Name 'framework-dependent' -OutputDir $FDOut -PublishArgs @('--self-contained', 'false')
}

if ($SelfContained) {
    Invoke-PublishProfile -Name 'self-contained' -OutputDir $SCOut -PublishArgs @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:TrimMode=partial', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false')
}

Write-Host 'Publish complete.'
