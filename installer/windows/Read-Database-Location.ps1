param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [Parameter(Mandatory = $true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $DataDirectory 'appsettings.Installed.json'
$directory = $DataDirectory
$fileName = 'restaurant.db'
if (Test-Path -LiteralPath $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settings.Database.Directory) { $directory = [string]$settings.Database.Directory }
    if ($settings.Database.FileName) { $fileName = [string]$settings.Database.FileName }
}
[System.IO.File]::WriteAllLines($OutputFile, @($directory, $fileName), [System.Text.UTF8Encoding]::new($false))
