param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [string] $ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('strands-managed-package-' + [Guid]::NewGuid().ToString('N'))),
    [ValidateSet('net8.0', 'net10.0', 'net48')][string[]] $TargetFramework = @('net8.0', 'net10.0'),
    [switch] $PrepareOnly,
    [string] $OfficialPackageSource = 'https://api.nuget.org/v3/index.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (($PrepareOnly -and ($TargetFramework.Count -ne 1 -or $TargetFramework[0] -ne 'net48')) -or
    ($TargetFramework -contains 'net48' -and -not $PrepareOnly)) {
    throw 'Use -TargetFramework net48 -PrepareOnly to prepare both Framework consumers without running on the build host.'
}
if (@($TargetFramework | Select-Object -Unique).Count -ne $TargetFramework.Count) { throw 'Target frameworks must be unique.' }
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$scratch = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'Use a new, dedicated package validation directory.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
New-Item -ItemType Directory -Path $scratch | Out-Null
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Get-ZipHash($Entry) {
    $stream = $Entry.Open(); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Invoke-ConsumerProcess([string] $File, [string[]] $Arguments, [string] $Log, [string] $WorkingDirectory, [hashtable] $Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    if ($onWindows) { $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden }
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $WorkingDirectory
    if ($null -ne $start.GetType().GetProperty('ArgumentList')) { foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) } }
    else {
        $quoted = @(foreach ($argument in $Arguments) {
            if ($argument.Contains('"')) { throw 'Quoted arguments are unsupported by the legacy Windows launcher.' }
            '"' + ($argument -replace '(\\+)$', '$1$1') + '"'
        })
        $start.Arguments = $quoted -join ' '
    }
    foreach ($name in @('ArtifactsPath', 'UseArtifactsOutput', 'NUGET_FALLBACK_PACKAGES', 'NUGET_RESTORE_MSBUILD_ARGS')) { $start.EnvironmentVariables.Remove($name) }
    foreach ($name in $Environment.Keys) { $start.EnvironmentVariables[$name] = [string] $Environment[$name] }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Failed to launch $File" }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if (Test-Path -LiteralPath $Log) { throw "Refusing to replace prior log: $Log" }
        [IO.File]::WriteAllText($Log, $text)
        if ($process.ExitCode -ne 0) { throw "Command failed ($($process.ExitCode)); retained log: $Log" }
        return $text
    } finally { $process.Dispose() }
}

$packages = @{}
foreach ($file in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $specs = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
        if ($specs.Count -ne 1) { throw 'Each feed package must contain exactly one nuspec.' }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml] $specification = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $id = [string] $specification.package.metadata.id
        if ($id -notin @('Strands.Agents', 'Strands.Agents.GoogleAuth')) { continue }
        if ($packages.ContainsKey($id)) { throw "Expected exactly one candidate package by nuspec identity: $id" }
        $version = [string] $specification.package.metadata.version
        if ([string]::IsNullOrWhiteSpace($version)) { throw "Missing package version: $id" }
        $packages[$id] = @{ Path = $file.FullName; Version = $version; SHA256 = Get-Sha256 $file.FullName }
    } finally { $zip.Dispose() }
}
if ($packages.Count -ne 2 -or $packages['Strands.Agents'].Version -cne $packages['Strands.Agents.GoogleAuth'].Version) { throw 'Matching Core and GoogleAuth candidate packages are required.' }
$version = $packages['Strands.Agents'].Version
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$escapedOfficialSource = [Security.SecurityElement]::Escape($OfficialPackageSource)
$configuration = "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"official`" value=`"$escapedOfficialSource`"/></packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"Strands.*`"/></packageSource><packageSource key=`"official`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>"
$projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>__TARGET_FRAMEWORK__</TargetFramework>
    <PlatformTarget>__PLATFORM__</PlatformTarget>
    <Prefer32Bit>false</Prefer32Bit>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Strands.Agents" Version="__VERSION__" />
__GOOGLE_PACKAGE__
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
    <Reference Include="System.Net.Http" />
  </ItemGroup>
</Project>
'@
$consumers = @()
foreach ($framework in $TargetFramework) {
    $platforms = if ($framework -eq 'net48') { @('x64', 'x86') } else { @('AnyCPU') }
    foreach ($platform in $platforms) {
        foreach ($family in @('Core', 'GoogleAuth')) {
            $variant = "$($family.ToLowerInvariant())-$framework-$($platform.ToLowerInvariant())"
            $projectRoot = Join-Path $scratch "consumers/$variant"
            # Caches are siblings of consumer projects; SDK content globs must not inspect cached TFMs.
            $isolation = Join-Path $scratch "isolation/$variant"
            $cache = Join-Path $isolation 'packages'; $temporary = Join-Path $isolation 'tmp'
            New-Item -ItemType Directory -Path $projectRoot, $cache, $temporary | Out-Null
            $project = Join-Path $projectRoot 'Consumer.csproj'
            $googleReference = if ($family -eq 'GoogleAuth') { "    <PackageReference Include=`"Strands.Agents.GoogleAuth`" Version=`"$([Security.SecurityElement]::Escape($version))`" />" } else { '' }
            $text = $projectTemplate.Replace('__TARGET_FRAMEWORK__', $framework).Replace('__PLATFORM__', $platform).Replace('__VERSION__', [Security.SecurityElement]::Escape($version)).Replace('__GOOGLE_PACKAGE__', $googleReference)
            [IO.File]::WriteAllText($project, $text + "`n")
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "validation/$family.cs.template") -Destination (Join-Path $projectRoot 'Program.cs')
            [IO.File]::WriteAllText((Join-Path $projectRoot 'NuGet.Config'), $configuration)
            $environment = @{ NUGET_PACKAGES = $cache; NUGET_HTTP_CACHE_PATH = (Join-Path $isolation 'http-cache'); DOTNET_CLI_HOME = (Join-Path $isolation 'dotnet-home'); TEMP = $temporary; TMP = $temporary; TMPDIR = $temporary; MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_USE_MSBUILD_SERVER = '0'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'; DOTNET_CLI_UI_LANGUAGE = 'en-US' }
            $null = Invoke-ConsumerProcess $dotnet @('restore', $project, '--configfile', (Join-Path $projectRoot 'NuGet.Config'), '--force', '--no-cache', '--disable-build-servers', '-nr:false', '-m:1') (Join-Path $projectRoot 'restore.log') $projectRoot $environment
            $published = Join-Path $projectRoot 'publish'
            $build = Invoke-ConsumerProcess $dotnet @('publish', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-nr:false', '-m:1', '-p:UseSharedCompilation=false', '--output', $published, '--verbosity', 'normal') (Join-Path $projectRoot 'publish.log') $projectRoot $environment
            if ($build -notmatch '0 Warning\(s\)' -or $build -notmatch '0 Error\(s\)' -or $build -match '(?m): warning ') { throw 'Managed package consumers must publish with zero warnings and errors.' }
            $assets = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') | ConvertFrom-Json
            if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count -ne 0) { throw 'Package consumers must not use ProjectReferences.' }
            $requiredPackages = if ($family -eq 'GoogleAuth') { @('Strands.Agents', 'Strands.Agents.GoogleAuth') } else { @('Strands.Agents') }
            $selected = @()
            foreach ($id in $requiredPackages) {
                $candidate = $packages[$id]
                $installed = Join-Path $cache ($id.ToLowerInvariant() + '/' + $version)
                $metadata = Get-Content -Raw -LiteralPath (Join-Path $installed '.nupkg.metadata') | ConvertFrom-Json
                if (-not [string]::Equals([IO.Path]::GetFullPath([string] $metadata.source).TrimEnd([IO.Path]::DirectorySeparatorChar), $feed.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or
                    (Get-Sha256 (Join-Path $installed ($id.ToLowerInvariant() + '.' + $version + '.nupkg'))) -cne $candidate.SHA256) { throw "Consumer package did not come byte-exact from the candidate feed: $id" }
                $key = "$id/$version"
                $targets = @(foreach ($target in $assets.targets.PSObject.Properties) { $entry = $target.Value.PSObject.Properties[$key]; if ($entry) { $entry.Value } })
                $assetFramework = if ($framework -eq 'net48') { 'netstandard2.0' } else { $framework }
                $assetPath = "lib/$assetFramework/$id.dll"
                if ($targets.Count -eq 0) { throw "Consumer selected an unexpected normal managed asset: $id" }
                foreach ($selection in $targets) {
                    if ($selection.compile.PSObject.Properties.Name -cnotcontains $assetPath -or $selection.runtime.PSObject.Properties.Name -cnotcontains $assetPath) { throw "Consumer selected an unexpected normal managed asset: $id" }
                }
                $zip = [IO.Compression.ZipFile]::OpenRead($candidate.Path)
                try {
                    $entry = $zip.GetEntry($assetPath)
                    if ($null -eq $entry -or (Get-ZipHash $entry) -cne (Get-Sha256 (Join-Path $published "$id.dll"))) { throw "Published managed bytes differ from the package: $id" }
                } finally { $zip.Dispose() }
                $selected += [pscustomobject] @{ Package = $id; Version = $version; NormalAsset = $assetPath; CandidateSHA256 = $candidate.SHA256; LocalOrigin = [string] $metadata.source }
            }
            $pointer = if ($platform -eq 'x86') { 4 } elseif ($platform -eq 'x64') { 8 } else { [IntPtr]::Size }
            $marker = if ($family -eq 'Core') { "PASS core-package-tool-cycle pointer=$pointer" } else { "PASS googleauth-package-provider-request pointer=$pointer" }
            $executable = Join-Path $published $(if ($framework -eq 'net48') { 'Consumer.exe' } else { 'Consumer.dll' })
            if (-not $PrepareOnly) {
                $result = Invoke-ConsumerProcess $dotnet @($executable, [string] $pointer) (Join-Path $projectRoot 'consumer.log') $projectRoot $environment
                if (@($result -split '\r?\n' | Where-Object { $_ -ceq $marker }).Count -ne 1) { throw "Missing unique public consumer success marker: $variant" }
                Write-Output $result
            }
            $consumers += [pscustomobject] @{ Family = $family; Framework = $framework; Platform = $platform; Pointer = $pointer; PublishedEntryPoint = $executable; SHA256 = Get-Sha256 $executable; SelectedAssets = $selected; RuntimeExecuted = -not $PrepareOnly; Marker = $marker; PublishWarnings = 0 }
        }
    }
}
[ordered] @{ Scope = 'Fresh package-only Core and GoogleAuth public scenarios; no cloud or live provider access'; PrepareOnly = [bool] $PrepareOnly; FrameworkQualification = 'Framework preparation does not prove runtime execution or distinguish Framework4.8 from4.8.1. Use the isolated runtime runner for that gate.'; Feed = $feed; Consumers = $consumers } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $scratch 'summary.json') -Encoding utf8
