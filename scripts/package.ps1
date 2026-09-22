param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime,
    [string]$OAuthClientPath
)

# Local-only packaging: no GitHub commands, tags, release creation, or hosted runners.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo "artifacts/packages/$Runtime-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$stage = Join-Path $output 'ProductivityMcp'
Push-Location $repo
try {
    dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    # RID-specific publishing needs a separate lock graph. Seed it from the source
    # lockfiles, and keep it in obj so packaging never rewrites checked-in locks.
    $locks = Get-ChildItem src, tests -Recurse -Filter packages.lock.json |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    $hashes = @{}
    foreach ($lock in $locks) {
        $hashes[$lock.FullName] = (Get-FileHash -LiteralPath $lock.FullName).Hash
        $obj = Join-Path $lock.DirectoryName 'obj'
        New-Item -ItemType Directory -Force -Path $obj | Out-Null
        Copy-Item -LiteralPath $lock.FullName -Destination (Join-Path $obj "publish.$Runtime.lock.json")
    }

    foreach ($project in @('App', 'Server')) {
        $destination = Join-Path $stage $project.ToLowerInvariant()
        dotnet publish "src/ProductivityMcp.$project" -c Release -r $Runtime --self-contained true `
            "-p:NuGetLockFilePath=obj/publish.$Runtime.lock.json" -p:RestoreLockedMode=false -o $destination
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }
    foreach ($lock in $locks) {
        if ((Get-FileHash -LiteralPath $lock.FullName).Hash -ne $hashes[$lock.FullName]) {
            throw "Publishing unexpectedly modified $($lock.FullName). Do not commit changed locks."
        }
    }

    if ($OAuthClientPath) {
        $client = Get-Content -LiteralPath $OAuthClientPath -Raw | ConvertFrom-Json
        if (-not $client.installed.client_id -or -not $client.installed.client_secret -or
            $client.access_token -or $client.refresh_token) {
            throw 'Expected a Google desktop OAuth client file, never a user token.'
        }
        foreach ($project in @('app', 'server')) {
            Copy-Item -LiteralPath $OAuthClientPath -Destination (Join-Path $stage "$project/google-oauth-client.json")
        }
    }
    Copy-Item README.md, LICENSE, CHANGELOG.md, SECURITY.md -Destination $stage
    Copy-Item docs -Destination $stage -Recurse
    Copy-Item docs/QUICKSTART.md -Destination (Join-Path $stage 'QUICKSTART.md')

    $archive = Join-Path $output "productivity-mcp-$Runtime"
    if ($Runtime.StartsWith('win-', [StringComparison]::Ordinal)) {
        $archive += '.zip'
        # ZipFile includes .playwright and all other runtime assets.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    } else {
        $archive += '.tar.gz'
        tar -czf $archive -C $output ProductivityMcp
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
    }
    $checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$checksum  $([IO.Path]::GetFileName($archive))`n")
    Write-Output "Local package: $archive"
    Write-Output "SHA-256: $checksum"
} finally {
    Pop-Location
}
