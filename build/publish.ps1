param(
    [switch]$SelfContained,
    [switch]$Admin
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Csproj = Join-Path $RepoRoot 'src\ProcessGuardian.Wpf\ProcessGuardian.Wpf.csproj'
$PublishRoot = Join-Path $RepoRoot 'publish'
$SCOut = Join-Path $PublishRoot 'self-contained'
$AdminOut = Join-Path $PublishRoot 'self-contained-admin'

$CommonArgs = @(
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:TrimMode=partial',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

function Invoke-PublishProfile {
    param(
        [string]$Name,
        [string]$Configuration,
        [string]$OutputDir,
        [string]$ExpectedExe
    )

    Write-Host "==> Publishing profile: $Name ($Configuration)"
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    dotnet publish $Csproj -c $Configuration -r win-x64 -o $OutputDir @CommonArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for profile '$Name' (exit code $LASTEXITCODE)"
    }

    $exe = Join-Path $OutputDir $ExpectedExe
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Expected executable not found: $exe"
    }

    $sizeMB = [Math]::Round(((Get-ChildItem -Recurse -File $OutputDir | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Host "    exe: $exe"
    Write-Host "    output size: $sizeMB MB"
}

if (-not ($SelfContained -or $Admin)) {
    $SelfContained = $true
    $Admin = $true
}

if ($SelfContained) {
    Invoke-PublishProfile -Name 'self-contained' -Configuration 'Release' -OutputDir $SCOut -ExpectedExe 'ProcessGuardian.exe'
}

if ($Admin) {
    Invoke-PublishProfile -Name 'self-contained-admin' -Configuration 'Release-Elevated' -OutputDir $AdminOut -ExpectedExe 'ProcessGuardianAdmin.exe'
}

Write-Host 'Publish complete.'
