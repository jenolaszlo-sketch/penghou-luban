param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][string] $IoFeedPath,
    [Parameter(Mandatory)][string] $Version
)
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path $PackageDirectory).Path
$feedRoot = (Resolve-Path $IoFeedPath).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("luban-package-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net8.0;net10.0</TargetFrameworks><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="Penghou.Luban" Version="[0.1.0-preview.1]" /></ItemGroup>
</Project>
'@.Replace('0.1.0-preview.1', $Version) | Set-Content -LiteralPath (Join-Path $scratch 'Consumer.csproj') -Encoding utf8
    @'
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Penghou.Luban.Language;

var compilation = LanguageCompiler.Compile("read src/a.txt | take 1", new WorkspaceId("smoke"));
if (!compilation.Succeeded) throw new InvalidOperationException("Public language compiler API failed.");
var versions = new LanguageVersions("2", "2", "windows-text-change-v2", "local-windows-read-v1");
var v2 = LanguageCompiler.Compile("#!luban2\ndiff src/before.txt src/after.txt", new WorkspaceId("smoke"), new LanguageCompilerOptions(Versions: versions));
if (!v2.Succeeded) throw new InvalidOperationException("Opt-in v2 compiler API failed.");
var diff = TextDiffEngine.Diff("before\n", "after\n");
if (diff.Status != TextDiffStatus.Succeeded) throw new InvalidOperationException("Public text diff API failed.");
_ = typeof(FileChangeRuntime);
_ = typeof(FileChangeCaptureBridge);
var referenced = typeof(LanguageCompiler).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);
if (referenced.Any(n => n.StartsWith("Penghou.IO.Local", StringComparison.Ordinal) || n.StartsWith("Penghou.Hufu", StringComparison.Ordinal) || n.StartsWith("Penghou.Fuwen", StringComparison.Ordinal) || n.StartsWith("Penghou.Zhinu", StringComparison.Ordinal)))
    throw new InvalidOperationException("Luban core package has an out-of-bound assembly reference.");
Console.WriteLine("Luban package-only consumer passed.");
'@ | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs') -Encoding utf8
    $cache = Join-Path $scratch 'packages'
    $configPath = Join-Path $scratch 'NuGet.Config'
    $packageSource = [System.Security.SecurityElement]::Escape($packageRoot)
    $ioSource = [System.Security.SecurityElement]::Escape($feedRoot)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="luban-candidate" value="$packageSource"/><add key="io-candidate" value="$ioSource"/></packageSources></configuration>
"@ | Set-Content -LiteralPath $configPath -Encoding utf8
    dotnet restore (Join-Path $scratch 'Consumer.csproj') --configfile $configPath --packages $cache
    if ($LASTEXITCODE -ne 0) { throw 'Package-only restore failed.' }
    foreach ($tfm in @('net8.0', 'net10.0')) {
        dotnet run --project (Join-Path $scratch 'Consumer.csproj') -c Release -f $tfm --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Package-only consumer failed ($tfm)." }
    }
} finally {
    $resolvedScratch = (Resolve-Path $scratch).Path
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $tempPrefix = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedScratch.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clean consumer scratch outside the temporary directory.' }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
