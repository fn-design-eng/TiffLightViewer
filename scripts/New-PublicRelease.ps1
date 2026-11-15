#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist ([IO.Path]::GetRandomFileName())
$publish = Join-Path $stage 'publish'
$archive = Join-Path $stage 'TiffLightViewer'
New-Item -ItemType Directory -Path $archive -Force | Out-Null

try {
    $project = Join-Path $root 'TiffLightViewer.csproj'
    $publishProperties = @('-p:PublishProfile=FrameworkDependent', '-p:PublishSingleFile=false', '-p:DebugType=None')
    dotnet publish $project @publishProperties -o $publish
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

    $hostInfo = dotnet msbuild $project -nologo @publishProperties -target:_GetAppHostPaths -getProperty:AppHostSourcePath -getItem:ResolvedAppHostPack
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the .NET apphost.' }
    $hostInfo = $hostInfo | Out-String | ConvertFrom-Json
    $hostPath = $hostInfo.Properties.AppHostSourcePath
    $hostPack = @($hostInfo.Items.ResolvedAppHostPack | Where-Object { $_.Path -eq $hostPath })
    if ($hostPack.Count -ne 1 -or -not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
        throw 'Cannot identify the exact .NET apphost pack.'
    }
    $hostDir = $hostPack[0].PackageDirectory
    if (-not (Test-Path (Join-Path $hostDir 'LICENSE.TXT')) -or
        -not (Test-Path (Join-Path $hostDir 'THIRD-PARTY-NOTICES.TXT'))) {
        # SDK-installed packs can omit the license files present in the NuGet package.
        $version = (Split-Path -Leaf $hostDir).ToLowerInvariant()
        if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected .NET apphost version.' }
        $id = 'microsoft.netcore.app.host.win-x64'
        $hostZip = Join-Path $stage 'apphost.zip'
        $hostDir = Join-Path $stage 'apphost'
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile $hostZip
        Expand-Archive -LiteralPath $hostZip -DestinationPath $hostDir
        $downloadedHost = Join-Path $hostDir $hostPack[0].PathInPackage
        if ((Get-FileHash -LiteralPath $hostPath).Hash -ne (Get-FileHash -LiteralPath $downloadedHost).Hash) {
            throw 'The NuGet apphost does not match the SDK apphost.'
        }
    }

    Copy-Item (Join-Path $hostDir 'LICENSE.TXT') (Join-Path $archive 'DOTNET-APPHOST-LICENSE.txt')
    Copy-Item (Join-Path $hostDir 'THIRD-PARTY-NOTICES.TXT') (Join-Path $archive 'DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt')
    foreach ($name in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'scripts/Install-TiffDecoder.ps1')) {
        Copy-Item (Join-Path $root $name) $archive
    }
    $zip = Join-Path $dist 'TiffLightViewer-win-x64.zip'
    Compress-Archive -LiteralPath $archive -DestinationPath $zip -Force
    Write-Output $zip
}
finally {
    # Only remove the unique staging directory created by this run.
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path -Parent $resolvedStage) -eq (Resolve-Path -LiteralPath $dist).Path) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
