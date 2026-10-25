[CmdletBinding()]
param(
    [string] $PackageDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/packages'),
    [string] $ScratchDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) ('strands-nuget-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
)

$ErrorActionPreference = 'Stop'
$sdkRoot = Split-Path $PSScriptRoot -Parent
$feed = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
$packages = @(Get-ChildItem -LiteralPath $feed -Filter 'Strands.Harness.*.nupkg' -File)
if ($packages.Count -ne 1) { throw "Expected exactly one Strands.Harness package in $feed; found $($packages.Count)." }

Add-Type -AssemblyName System.IO.Compression.ZipFile
$archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $specs = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
    if ($specs.Count -ne 1) { throw 'The harness package must contain exactly one root nuspec.' }
    $reader = [System.IO.StreamReader]::new($specs[0].Open())
    try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $spec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $id = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
    $version = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
    if ($id -ne 'Strands.Harness' -or [string]::IsNullOrWhiteSpace($version)) { throw 'Invalid harness package identity.' }
} finally { $archive.Dispose() }

$scratch = [System.IO.Path]::GetFullPath($ScratchDirectory)
if ((Test-Path -LiteralPath $scratch) -and @(Get-ChildItem -LiteralPath $scratch -Force).Count -gt 0) {
    throw 'ScratchDirectory must be new or empty to prevent stale package reuse.'
}
$consumer = Join-Path $scratch 'consumer'
# Keep the restore cache beside the project so SDK content globs cannot include package assets.
$cache = Join-Path $scratch 'packages'
[void] [System.IO.Directory]::CreateDirectory($consumer)
'<Project />' | Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.props') -Encoding utf8
$project = Join-Path $consumer 'Consumer.csproj'
$config = Join-Path $scratch 'NuGet.Config'
$escapedVersion = [System.Security.SecurityElement]::Escape($version)
$escapedFeed = [System.Security.SecurityElement]::Escape($feed)

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net48;net8.0;net10.0</TargetFrameworks>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Strands.Harness" Version="[$escapedVersion]" />
  </ItemGroup>
  <ItemGroup Condition="'`$(TargetFramework)' == 'net48'">
    <Reference Include="System.Net.Http" />
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Strands.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8
Copy-Item -LiteralPath (Join-Path $sdkRoot 'examples/GettingStarted/Program.cs') -Destination (Join-Path $consumer 'Program.cs')
Write-Host "Validating Strands.Harness $version using scratch directory $scratch"

$previousCliHome = $env:DOTNET_CLI_HOME
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
$previousCertificateSetting = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_CLI_HOME = Join-Path $scratch 'dotnet-home'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $scratch 'http-cache'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
try {
    & dotnet restore $project --configfile $config --packages $cache
    if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed with exit code $LASTEXITCODE." }
    & dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Package consumer build failed with exit code $LASTEXITCODE." }
    $frameworks = @('net8.0', 'net10.0')
    if ($IsWindows) { $frameworks += 'net48' }
    foreach ($framework in $frameworks) {
        $lines = & dotnet run --project $project --configuration Release --framework $framework --no-build --no-restore -- --demo 2>&1
        $exitCode = $LASTEXITCODE
        $lines | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) { throw "Package consumer $framework failed with exit code $exitCode." }
        if ($lines -notcontains '2 + 3 = 5.' -or $lines -notcontains 'Stop: end_turn; model turns: 2') {
            throw "Package consumer $framework did not produce the expected tool-cycle result."
        }
    }
    # Prepare architecture-specific full Harness consumers for the isolated Framework runner.
    # Do not execute them on a host with an in-place 4.8.1 runtime.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        foreach ($platform in @('x64', 'x86')) {
            $root = Join-Path $scratch "framework-$platform"
            $source = Join-Path $root 'source'
            $output = Join-Path $root 'output'
            New-Item -ItemType Directory -Path $source | Out-Null
            $frameworkProject = Join-Path $source 'Consumer.csproj'
            Copy-Item -LiteralPath $project -Destination $frameworkProject
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'validation/HarnessFramework48Consumer.cs.template') -Destination (Join-Path $source 'Program.cs')
            & dotnet restore $frameworkProject --configfile $config --packages $cache -p:TargetFrameworks=net48 "-p:PlatformTarget=$platform" 2>&1 | Tee-Object -FilePath (Join-Path $root 'restore.log')
            if ($LASTEXITCODE -ne 0) { throw "Full Harness consumer net48 $platform restore failed ($LASTEXITCODE)." }
            & dotnet build $frameworkProject --configuration Release --framework net48 --no-restore -p:TargetFrameworks=net48 "-p:PlatformTarget=$platform" --output $output 2>&1 | Tee-Object -FilePath (Join-Path $root 'build.log')
            if ($LASTEXITCODE -ne 0) { throw "Full Harness consumer net48 $platform build failed ($LASTEXITCODE)." }
            $assets = Get-Content -Raw -LiteralPath (Join-Path $source 'obj/project.assets.json') | ConvertFrom-Json
            $target = $assets.targets.PSObject.Properties['net48'].Value
            if ($null -eq $target) { $target = $assets.targets.PSObject.Properties['.NETFramework,Version=v4.8'].Value }
            if ($null -eq $target) { throw 'The full Harness package consumer is missing its net48 restore target.' }
            $evidence = foreach ($id in @('Strands.Harness', 'Strands.Agents')) {
                $selected = @($target.PSObject.Properties | Where-Object { $_.Name.StartsWith("$id/", [StringComparison]::Ordinal) })
                $asset = "lib/netstandard2.0/$id.dll"
                if ($selected.Count -ne 1 -or $selected[0].Value.compile.PSObject.Properties.Name -cnotcontains $asset -or $selected[0].Value.runtime.PSObject.Properties.Name -cnotcontains $asset) {
                    throw "The full Harness consumer must select the normal netstandard2.0 $id compile and runtime assets."
                }
                $installed = Join-Path $cache ($selected[0].Name.ToLowerInvariant() + '/' + $asset)
                $copied = Join-Path $output "$id.dll"
                $digest = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
                if ((Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash -cne $digest) { throw "The full Harness consumer copied a different $id asset." }
                [pscustomobject]@{ Package = $selected[0].Name; CompileAsset = $asset; RuntimeAsset = $asset; InstalledPath = $installed; OutputPath = $copied; SHA256 = $digest }
            }
            $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'package-assets.json') -Encoding utf8
        }
    }
    Write-Host "Package consumption passed for $($frameworks -join ', '). Scratch files retained at $scratch"
} finally {
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificateSetting
}
