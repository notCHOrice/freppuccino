param(
    [ValidateSet('any', 'win', 'mac')]
    [string]$Platform = 'any',
    [string]$Version,
    [string]$Yak
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Freppuccino\Freppuccino.csproj'
$outDir = Join-Path $root 'src\Freppuccino\bin\Release\net10.0'
$artifacts = Join-Path $root 'artifacts\yak'
$stage = Join-Path $artifacts 'stage'

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

dotnet build $project -c Release -clp:NoSummary -p:IncludeSourceRevisionInInformationalVersion=false -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

if (-not (Test-Path (Join-Path $outDir 'ui\index.html'))) { throw 'Side panel UI missing from build output (src/ui/dist). Run npm ci in src/ui.' }

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item -Path (Join-Path $outDir '*') -Destination $stage -Recurse
Get-ChildItem -LiteralPath $stage -Recurse -File -Filter *.pdb | Remove-Item -Force

$rids = switch ($Platform) {
    'win' { @('win-x64', 'win-arm64') }
    'mac' { @('osx-x64', 'osx-arm64') }
    default { @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64') }
}
Get-ChildItem -LiteralPath (Join-Path $stage 'runtimes') -Directory | Where-Object { $rids -notcontains $_.Name } | Remove-Item -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.yml'), (Join-Path $PSScriptRoot 'icon.png') -Destination $stage
$misc = New-Item -ItemType Directory -Path (Join-Path $stage 'misc')
Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') -Destination $misc

Get-ChildItem -LiteralPath $artifacts -Filter *.yak -ErrorAction SilentlyContinue | Remove-Item -Force

Push-Location $stage
try {
    & $Yak build --platform $Platform --version $Version
    if ($LASTEXITCODE -ne 0) { throw 'yak build failed.' }
    Get-ChildItem -Filter *.yak | Move-Item -Destination $artifacts -Force
}
finally {
    Pop-Location
}

$package = Get-ChildItem -LiteralPath $artifacts -Filter *.yak | Select-Object -First 1
Write-Host "Built $($package.FullName)"
