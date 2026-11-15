#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist ([IO.Path]::GetRandomFileName())
$publish = Join-Path $stage 'publish'
$archive = Join-Path $stage 'archive'
New-Item -ItemType Directory -Path $archive -Force | Out-Null

try {
    dotnet publish (Join-Path $root 'TiffLightViewer.csproj') -p:PublishProfile=FrameworkDependent -p:PublishSingleFile=false -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $appFiles = @('TiffLightViewer.exe', 'TiffLightViewer.dll', 'TiffLightViewer.deps.json', 'TiffLightViewer.runtimeconfig.json')
    $deps = Get-Content (Join-Path $publish 'TiffLightViewer.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $target = $deps.targets[$deps.runtimeTarget.name]
    $packages = @($target.Keys | Where-Object { $_ -match '^Magick\.NET(-Q8-AnyCPU|\.Core)/' })
    if ($packages.Count -ne 2 -or $target.Count -ne 3) { throw 'Unexpected release dependencies.' }

    $files = foreach ($package in $packages) {
        $id, $version = $package.Split('/')
        foreach ($kind in @('runtime', 'native')) {
            if (-not $target[$package][$kind]) { continue }
            foreach ($entry in $target[$package][$kind].Keys) {
                $name = [IO.Path]::GetFileName($entry)
                @{
                    package = $id.ToLowerInvariant()
                    version = $version
                    entry = $entry
                    name = $name
                    sha256 = (Get-FileHash (Join-Path $publish $name) -Algorithm SHA256).Hash
                }
            }
        }
    }
    $expected = $appFiles + @($files | ForEach-Object { $_.name })
    if (Compare-Object $expected @(Get-ChildItem $publish -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($publish, $_.FullName) })) {
        throw 'Publish layout changed; review the release contents.'
    }

    foreach ($name in $appFiles) {
        Copy-Item -LiteralPath (Join-Path $publish $name) -Destination $archive
    }
    $files | ConvertTo-Json -AsArray | Set-Content (Join-Path $archive 'decoder-files.json') -Encoding utf8

    $assets = Get-Content (Join-Path $root 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $hostPack = $assets.project.frameworks.Values.downloadDependencies |
        Where-Object { $_.name -eq 'Microsoft.NETCore.App.Host.win-x64' } | Select-Object -First 1
    if (-not $hostPack -or $hostPack.version -notmatch '^\[([^,]+), \1\]$') {
        throw 'Cannot identify the exact .NET apphost pack.'
    }
    $packPath = "microsoft.netcore.app.host.win-x64/$($Matches[1])"
    $hostDir = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $packPath } |
        Where-Object { Test-Path (Join-Path $_ 'LICENSE.TXT') } | Select-Object -First 1
    if (-not $hostDir) { throw 'Missing .NET apphost license.' }

    Copy-Item (Join-Path $hostDir 'LICENSE.TXT') (Join-Path $archive 'DOTNET-APPHOST-LICENSE.txt')
    Copy-Item (Join-Path $hostDir 'THIRD-PARTY-NOTICES.TXT') (Join-Path $archive 'DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt')
    foreach ($name in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'scripts/Install-TiffDecoder.ps1')) {
        Copy-Item (Join-Path $root $name) $archive
    }
    $zip = Join-Path $dist 'TiffLightViewer-win-x64.zip'
    Compress-Archive -Path (Join-Path $archive '*') -DestinationPath $zip -Force
    Write-Output $zip
}
finally {
    # Only remove the unique staging directory created by this run.
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path -Parent $resolvedStage) -eq (Resolve-Path -LiteralPath $dist).Path) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
