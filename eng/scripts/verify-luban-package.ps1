param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][string] $Version,
    [string] $IoVersion = '0.1.0-preview.1'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path $PackageDirectory).Path
$nupkgPath = Join-Path $root "Penghou.Luban.$Version.nupkg"
$snupkgPath = Join-Path $root "Penghou.Luban.$Version.snupkg"
if (!(Test-Path -LiteralPath $nupkgPath) -or !(Test-Path -LiteralPath $snupkgPath)) { throw 'Expected nupkg and snupkg were not produced.' }
$archive = [IO.Compression.ZipFile]::OpenRead($nupkgPath)
 $symbols = [IO.Compression.ZipFile]::OpenRead($snupkgPath)
try {
    if (!($symbols.Entries | Where-Object FullName -Like '*.pdb')) { throw 'Symbol package does not contain PDB files.' }
    $nuspecEntry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
    if ($nuspecEntry.Count -ne 1) { throw 'Package must contain one nuspec.' }
    $reader = [IO.StreamReader]::new($nuspecEntry[0].Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $nuspec.package.metadata
    if ($metadata.id -ne 'Penghou.Luban' -or $metadata.version -ne $Version) { throw 'Package identity/version mismatch.' }
    if ($metadata.license.type -ne 'expression' -or $metadata.license.InnerText -ne 'Apache-2.0') { throw 'Apache-2.0 license expression missing.' }
    if ($metadata.repository.url -ne 'https://github.com/jenolaszlo-sketch/penghou-luban') { throw 'Repository metadata missing or incorrect.' }
    if ($metadata.readme -ne 'README.md') { throw 'Packaged README metadata missing.' }
    if (!($archive.Entries | Where-Object FullName -eq 'README.md')) { throw 'Packaged README.md missing.' }
    $groups = @($metadata.dependencies.group | ForEach-Object targetFramework)
    if (!($groups | Where-Object { $_ -like '*net8.0*' -or $_ -like '*Version=v8.0*' }) -or !($groups | Where-Object { $_ -like '*net10.0*' -or $_ -like '*Version=v10.0*' })) { throw 'Package must declare net8.0 and net10.0 dependency groups.' }
    foreach ($group in @($metadata.dependencies.group)) {
        $deps = @($group.dependency)
        if ($deps.Count -ne 2) { throw 'Core package must have only the two declared IO dependencies.' }
        foreach ($id in @('Penghou.IO.Abstractions', 'Penghou.IO.Protocols')) {
            $found = @($deps | Where-Object { $_.id -eq $id -and $_.version -eq "[$IoVersion]" })
            if ($found.Count -ne 1) { throw "Dependency group '$($group.targetFramework)' must pin exactly one $id at [$IoVersion]." }
        }
        if (@($deps | Where-Object id -eq 'Penghou.IO.Local').Count -gt 0) { throw 'Core package must not depend on Penghou.IO.Local.' }
    }
    foreach ($tfm in @('net8.0', 'net10.0')) { if (!($archive.Entries | Where-Object FullName -Like "lib/$tfm/*")) { throw "Missing $tfm library asset." } }
} finally { $archive.Dispose(); $symbols.Dispose() }
