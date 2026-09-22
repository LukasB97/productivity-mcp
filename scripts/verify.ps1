param([switch]$InstallRenderer, [switch]$SkipRenderer)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build ProductivityMcp.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    if ($InstallRenderer) {
        & ./src/ProductivityMcp.App/bin/Release/net10.0/playwright.ps1 install chromium
        if ($LASTEXITCODE -ne 0) { throw 'Renderer installation failed.' }
    }
    $filter = if ($SkipRenderer) { 'TestCategory!=Live&TestCategory!=Renderer' } else { 'TestCategory!=Live' }
    dotnet test --project tests/ProductivityMcp.Tests/ProductivityMcp.Tests.csproj -c Release --no-build --no-restore --filter $filter
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet format ProductivityMcp.sln --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting check failed.' }
} finally {
    Pop-Location
}
