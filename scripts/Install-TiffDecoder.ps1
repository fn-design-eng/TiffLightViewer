#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$manifest = Get-Content (Join-Path $PSScriptRoot 'decoder-files.json') -Raw | ConvertFrom-Json
$client = New-Object System.Net.WebClient
try {
    foreach ($group in ($manifest | Group-Object package, version)) {
        $first = $group.Group[0]
        $id = $first.package
        $version = $first.version
        if ($id -notin @('magick.net-q8-anycpu', 'magick.net.core') -or $version -notmatch '^\d+\.\d+\.\d+$') {
            throw 'Unexpected decoder package.'
        }
        $url = "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
        $bytes = $client.DownloadData($url)
        $stream = [IO.MemoryStream]::new($bytes, $false)
        $zip = [IO.Compression.ZipArchive]::new($stream)
        try {
            foreach ($file in $group.Group) {
                if ($file.name -notin @('Magick.NET-Q8-AnyCPU.dll', 'Magick.NET.Core.dll', 'Magick.Native-Q8-x64.dll')) {
                    throw 'Unexpected decoder file.'
                }
                $entry = $zip.GetEntry($file.entry)
                if (-not $entry) { throw "Missing NuGet entry: $($file.entry)" }
                $source = $entry.Open()
                $buffer = [IO.MemoryStream]::new()
                try {
                    $source.CopyTo($buffer)
                    $payload = $buffer.ToArray()
                    $buffer.Position = 0
                    if ((Get-FileHash -InputStream $buffer -Algorithm SHA256).Hash -ne $file.sha256) {
                        throw "Decoder checksum mismatch: $($file.name)"
                    }
                    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot $file.name), $payload)
                }
                finally {
                    $buffer.Dispose()
                    $source.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    Write-Host 'TIFF decoder installed.'
}
finally {
    $client.Dispose()
}
