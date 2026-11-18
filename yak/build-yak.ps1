param(
    [ValidateSet(8, 9)]
    [int[]]$Rhino = @(9, 8),
    [ValidateSet('any', 'win', 'mac')]
    [string]$Platform,
    [string]$Version,
    [string]$Yak,
    [string]$Repo
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Freppuccino\Freppuccino.csproj'
$artifacts = Join-Path $root 'artifacts\yak'
$targets = @{
    9 = @{ Framework = 'net10.0'; Platform = 'any' }
    8 = @{ Framework = 'net8.0'; Platform = 'win' }
}

if (-not $Yak) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Rhino 9\System\Yak.exe'),
        (Get-Command yak -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path $_) }
    if (-not $candidates) { throw 'Yak.exe not found. Install Rhino 9 or pass -Yak <path to Yak.exe>.' }
    $Yak = @($candidates)[0]
}

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }
Get-ChildItem -LiteralPath $artifacts -Filter *.yak -ErrorAction SilentlyContinue | Remove-Item -Force

$packages = @()
foreach ($major in $Rhino) {
    $framework = $targets[$major].Framework
    $targetPlatform = if ($Platform) { $Platform } else { $targets[$major].Platform }
    $outDir = Join-Path $root "src\Freppuccino\bin\Release\$framework"
    $stage = Join-Path $artifacts "stage-rh$major"

    dotnet build $project -c Release -f $framework -clp:NoSummary -p:IncludeSourceRevisionInInformationalVersion=false -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for Rhino $major ($framework)." }
    if (-not (Test-Path (Join-Path $outDir 'ui\index.html'))) { throw 'Side panel UI missing from build output (src/ui/dist). Run npm ci in src/ui.' }

    if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $outDir '*') -Destination $stage -Recurse
    Get-ChildItem -LiteralPath $stage -Recurse -File -Filter *.pdb | Remove-Item -Force

    $rids = switch ($targetPlatform) {
        'win' { @('win-x64', 'win-arm64') }
        'mac' { @('osx-x64', 'osx-arm64') }
        default { @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64') }
    }
    Get-ChildItem -LiteralPath (Join-Path $stage 'runtimes') -Directory | Where-Object { $rids -notcontains $_.Name } | Remove-Item -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.yml'), (Join-Path $PSScriptRoot 'icon.png') -Destination $stage
    $misc = New-Item -ItemType Directory -Path (Join-Path $stage 'misc')
    Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') -Destination $misc

    Push-Location $stage
    try {
        & $Yak build --platform $targetPlatform --version $Version
        if ($LASTEXITCODE -ne 0) { throw "yak build failed for Rhino $major." }
        $built = Get-ChildItem -Filter *.yak | Select-Object -First 1
        Move-Item -LiteralPath $built.FullName -Destination $artifacts -Force
        $packages += Get-Item (Join-Path $artifacts $built.Name)
    }
    finally {
        Pop-Location
    }
    Write-Host "Built $(Join-Path $artifacts $built.Name)"
}

if ($Repo) {
    if (-not (Test-Path -LiteralPath $Repo)) { New-Item -ItemType Directory -Path $Repo | Out-Null }
    foreach ($package in $packages) {
        if (Test-Path -LiteralPath (Join-Path $Repo $package.Name)) { throw "$($package.Name) already exists in $Repo. Bump <Version> in Freppuccino.csproj or pass -Version." }
    }
    foreach ($package in $packages) { Copy-Item -LiteralPath $package.FullName -Destination $Repo }
    Push-Location $Repo
    try {
        & $Yak cache
        if ($LASTEXITCODE -ne 0) { throw 'yak cache failed.' }
    }
    finally {
        Pop-Location
    }
    Write-Host "Published $($packages.Name -join ', ') to $Repo"
}
