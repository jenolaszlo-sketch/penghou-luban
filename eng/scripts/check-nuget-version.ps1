param(
    [Parameter(Mandatory = $true)]
    [string] $PackageVersion,

    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [switch] $RequirePublished
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageId = 'Penghou.Luban'
$packageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
$httpClient = [System.Net.Http.HttpClient]::new()
$verified = $false

function Get-PackageManifest([System.IO.Compression.ZipArchive] $archive) {
    $files = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
    $signatureCount = 0
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -eq '.signature.p7s') {
            $signatureCount++
            continue
        }
        if ($files.ContainsKey($entry.FullName)) {
            throw "NuGet package contains a duplicate ZIP entry '$($entry.FullName)'."
        }
        $stream = $entry.Open()
        try {
            $digest = [System.Security.Cryptography.SHA256]::HashData($stream)
            $files.Add($entry.FullName, [Convert]::ToHexString($digest))
        }
        finally {
            $stream.Dispose()
        }
    }
    if ($signatureCount -gt 1) {
        throw 'NuGet package contains multiple repository-signature entries.'
    }
    return @{ Files = $files }
}

try {
    $indexUri = "https://api.nuget.org/v3-flatcontainer/$($packageId.ToLowerInvariant())/index.json"
    $versions = @()
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $response = $httpClient.GetAsync($indexUri).GetAwaiter().GetResult()
        try {
            if ($response.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) {
                $response.EnsureSuccessStatusCode()
                $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                $versions = @($json.versions)
            }
        }
        finally {
            $response.Dispose()
        }
        if ($PackageVersion -in $versions -or -not $RequirePublished -or $attempt -eq 12) { break }
        Start-Sleep -Seconds 15
    }

    if ($PackageVersion -notin $versions) {
        if ($RequirePublished) {
            throw "$packageId $PackageVersion was not visible on NuGet.org within the bounded indexing window after publication. Rerun the publish job with the same validated artifact after NuGet indexing completes."
        }
        Write-Output "$packageId $PackageVersion is not present on NuGet.org."
        return
    }

    $fileName = "$packageId.$PackageVersion.nupkg"
    $localPath = Join-Path $packageDirectory $fileName
    if (-not (Test-Path -LiteralPath $localPath)) {
        throw "Validated release artifact '$fileName' is missing."
    }

    $packageUri = "https://api.nuget.org/v3-flatcontainer/$($packageId.ToLowerInvariant())/$($PackageVersion.ToLowerInvariant())/$($fileName.ToLowerInvariant())"
    $packageResponse = $null
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $packageResponse = $httpClient.GetAsync($packageUri).GetAwaiter().GetResult()
        if ($packageResponse.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) { break }
        $packageResponse.Dispose()
        if (-not $RequirePublished -or $attempt -eq 12) { break }
        Start-Sleep -Seconds 15
    }
    if ($null -eq $packageResponse -or $packageResponse.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
        if ($null -ne $packageResponse) { $packageResponse.Dispose() }
        throw "$packageId $PackageVersion appears in the NuGet version index, but its package content is not downloadable within the bounded retry window. Wait for indexing before verifying or retrying publication."
    }
    $packageResponse.EnsureSuccessStatusCode()
    $remoteBytes = $packageResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $packageResponse.Dispose()
    $remoteStream = [System.IO.MemoryStream]::new([byte[]] $remoteBytes, $false)
    $remoteArchive = [System.IO.Compression.ZipArchive]::new($remoteStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
    $localArchive = [System.IO.Compression.ZipFile]::OpenRead($localPath)
    try {
        $remoteManifest = Get-PackageManifest $remoteArchive
        $localManifest = Get-PackageManifest $localArchive
        if ($remoteManifest.Files.Count -ne $localManifest.Files.Count) {
            throw "$packageId $PackageVersion already has different package contents on NuGet.org. Do not overwrite immutable package content; retry with the same validated artifact or choose a new coordinated version."
        }
        foreach ($entryName in $localManifest.Files.Keys) {
            if (-not $remoteManifest.Files.ContainsKey($entryName) -or
                $remoteManifest.Files[$entryName] -ne $localManifest.Files[$entryName]) {
                throw "$packageId $PackageVersion already has different package contents on NuGet.org. Do not overwrite immutable package content; retry with the same validated artifact or choose a new coordinated version."
            }
        }
    }
    finally {
        $localArchive.Dispose()
        $remoteArchive.Dispose()
        $remoteStream.Dispose()
    }
    $verified = $true
    Write-Output "$packageId $PackageVersion package contents match the validated artifact on NuGet.org (repository signature excluded)."

    if ($RequirePublished -and -not $verified) {
        throw "$packageId $PackageVersion was not verified on NuGet.org."
    }
}
finally {
    $httpClient.Dispose()
}
