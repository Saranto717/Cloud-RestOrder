param(
    [string]$Version = '17.3.132'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'server\RestaurantOrders.csproj'
$publish = Join-Path $repo "artifacts\publish\win-x64-$Version"
$output = Join-Path $repo 'artifacts\installer'
if (Test-Path $publish) { throw "Publish folder already exists; use a new version or move it aside: $publish" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$forbidden = Get-ChildItem $publish -Recurse -File | Where-Object {
    $_.Extension -in @('.cs', '.razor', '.csproj', '.sln', '.pdb', '.db', '.sqlite', '.log') -or $_.Name -match '\.db-(wal|shm)$'
}
if ($forbidden) { throw ('Publish contains forbidden files: ' + (($forbidden | ForEach-Object { $_.FullName.Substring($publish.Length + 1) }) -join ', ')) }
$compilerCandidates = if ($env:INNO_SETUP_COMPILER) {
    @($env:INNO_SETUP_COMPILER)
} else {
    @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno\ISCC.exe')
    )
}
$compiler = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$compiler) { throw 'Install Inno Setup 6 or set INNO_SETUP_COMPILER to the full path of ISCC.exe.' }
& $compiler "/DPublishDir=$publish" "/DOutputDir=$output" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'RestaurantOrders.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output "RestaurantOrders-Setup-$Version-win-x64.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -Path ($setup + '.sha256') -Encoding ASCII
Write-Host "Installer ready: $setup"




