param(
    [Parameter(Mandatory)][string] $RepositoryPath,
    [Parameter(Mandatory)][string] $FeedPath
)
$ErrorActionPreference = 'Stop'
$expectedCommit = '89858631e4f9220bf40506cff34bb9b6ee8912dd'
$actualCommit = (git -C $RepositoryPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $expectedCommit) { throw "IO candidate checkout must be $expectedCommit (got $actualCommit)." }
New-Item -ItemType Directory -Force -Path $FeedPath | Out-Null
$feed = (Resolve-Path $FeedPath).Path
$cacheRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$cache = Join-Path $cacheRoot 'io-candidate-nuget-cache'
foreach ($project in @('Penghou.IO.Abstractions', 'Penghou.IO.Protocols', 'Penghou.IO.Local')) {
    $projectFile = Join-Path $RepositoryPath "src/$project/$project.csproj"
    dotnet pack $projectFile -c Release -p:PackageVersion=0.1.0-preview.1 "-p:RestorePackagesPath=$cache" -o $feed
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
}
$packages = Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.symbols.nupkg' }
foreach ($id in @('Penghou.IO.Abstractions', 'Penghou.IO.Protocols', 'Penghou.IO.Local')) {
    $package = @($packages | Where-Object Name -eq "$id.0.1.0-preview.1.nupkg")
    if ($package.Count -ne 1) { throw "Candidate feed must contain exactly one $id 0.1.0-preview.1 package." }
}
# Use a configuration file instead of repeated CLI --source arguments; this
# also pins IO identities to the candidate feed rather than a public fallback.
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="io-candidate" value="$escapedFeed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="io-candidate"><package pattern="Penghou.IO.Abstractions"/><package pattern="Penghou.IO.Protocols"/><package pattern="Penghou.IO.Local"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@
$config | Set-Content -LiteralPath (Join-Path $feed 'NuGet.Config') -Encoding utf8
