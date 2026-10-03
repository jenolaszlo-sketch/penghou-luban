param(
    [Parameter(Mandatory = $true)]
    [string] $PackageVersion,

    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [switch] $RequirePublished,

    [ValidateRange(0, 3600)]
    [int] $IndexingTimeoutSeconds = 300,

    [ValidateRange(1, 60)]
    [int] $RetryIntervalSeconds = 15
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageIds = @('Penghou.Luban')
$packageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
$httpClient = [System.Net.Http.HttpClient]::new()
$verifiedCount = 0
$indexingClock = [System.Diagnostics.Stopwatch]::StartNew()

function Wait-ForIndexing([string] $description) {
    if (-not $RequirePublished) { return $false }
    $remainingSeconds = $IndexingTimeoutSeconds - $indexingClock.Elapsed.TotalSeconds
    if ($remainingSeconds -le 0) { return $false }
    $delayMilliseconds = [int][Math]::Min($RetryIntervalSeconds * 1000, $remainingSeconds * 1000)
    if ($delayMilliseconds -le 0) { return $false }
    Write-Output "$description is not available yet; waiting for NuGet indexing." | Out-Host
    Start-Sleep -Milliseconds $delayMilliseconds
    return $true
}

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
    return @{ Files = $files; SignatureCount = $signatureCount }
}

try {
    foreach ($id in $packageIds) {
        $uri = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json"
        $versions = @()
        while ($true) {
            $response = $httpClient.GetAsync($uri).GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) {
                    [void] $response.EnsureSuccessStatusCode()
                    $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    $versions = @($json.versions)
                }
            }
            finally {
                $response.Dispose()
            }
            if ($PackageVersion -in $versions -or -not (Wait-ForIndexing "$id $PackageVersion version index")) { break }
        }
        if ($PackageVersion -notin $versions) {
            if ($RequirePublished) {
                throw "$id $PackageVersion was not visible on NuGet.org within the shared $IndexingTimeoutSeconds-second indexing window. Upload may already have succeeded. Re-run only the verification job with the same validated artifact after NuGet indexing completes."
            }
            Write-Output "$id $PackageVersion is not present on NuGet.org."
            continue
        }

        $fileName = "$id.$PackageVersion.nupkg"
        $localPath = Join-Path $packageDirectory $fileName
        if (-not (Test-Path -LiteralPath $localPath)) {
            throw "Validated release artifact '$fileName' is missing."
        }
        $packageUri = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/$($PackageVersion.ToLowerInvariant())/$($fileName.ToLowerInvariant())"
        $packageResponse = $null
        while ($true) {
            $packageResponse = $httpClient.GetAsync($packageUri).GetAwaiter().GetResult()
            if ($packageResponse.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) { break }
            if (-not (Wait-ForIndexing "$id $PackageVersion package content")) { break }
            $packageResponse.Dispose()
        }
        if ($packageResponse.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
            $packageResponse.Dispose()
            throw "$id $PackageVersion appears in the NuGet version index, but its package content is not downloadable yet. Wait for indexing and rerun only the verification job using the same validated release artifact."
        }
        try {
            [void] $packageResponse.EnsureSuccessStatusCode()
            $remoteBytes = $packageResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        }
        finally {
            $packageResponse.Dispose()
        }
        $remoteStream = [System.IO.MemoryStream]::new([byte[]] $remoteBytes, $false)
        $remoteArchive = [System.IO.Compression.ZipArchive]::new($remoteStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
        $localArchive = [System.IO.Compression.ZipFile]::OpenRead($localPath)
        try {
            $remoteManifest = Get-PackageManifest $remoteArchive
            $localManifest = Get-PackageManifest $localArchive
            if ($remoteManifest.Files.Count -ne $localManifest.Files.Count) {
                throw "$id $PackageVersion already has different package contents on NuGet.org. Do not overwrite immutable package content; use the same validated artifact for a retry or choose a new coordinated version."
            }
            foreach ($entryName in $localManifest.Files.Keys) {
                if (-not $remoteManifest.Files.ContainsKey($entryName) -or
                    $remoteManifest.Files[$entryName] -ne $localManifest.Files[$entryName]) {
                    throw "$id $PackageVersion already has different package contents on NuGet.org. Do not overwrite immutable package content; use the same validated artifact for a retry or choose a new coordinated version."
                }
            }
        }
        finally {
            $localArchive.Dispose()
            $remoteArchive.Dispose()
            $remoteStream.Dispose()
        }
        $verifiedCount++
        Write-Output "$id $PackageVersion package contents match the validated artifact on NuGet.org (repository signature excluded)."
    }

    if ($RequirePublished -and $verifiedCount -ne $packageIds.Count) {
        throw "Only $verifiedCount of $($packageIds.Count) package versions were verified on NuGet.org."
    }
}
finally {
    $httpClient.Dispose()
}
