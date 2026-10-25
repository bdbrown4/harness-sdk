param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [string] $ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('strands-audio-' + [Guid]::NewGuid().ToString('N'))),
    [ValidateSet('all', 'net8.0', 'net10.0', 'net48')][string] $TargetFramework = 'all',
    [ValidateSet('x64', 'x86')][string[]] $FrameworkArchitectures = @('x64', 'x86')
)
function Get-ExactAudioPackage([string] $Directory) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $matches = @(foreach ($candidate in Get-ChildItem -LiteralPath $Directory -Filter '*.nupkg' -File) {
        $zip = [IO.Compression.ZipFile]::OpenRead($candidate.FullName)
        try {
            $entries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
            if ($entries.Count -ne 1) { throw 'Expected one candidate package manifest.' }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { [xml] $specification = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ([string] $specification.package.metadata.id -ieq 'Strands.Agents.Bidi.Audio') { $candidate }
        } finally { $zip.Dispose() }
    })
    if ($matches.Count -ne 1) { throw 'Expected exactly one Audio package by nuspec identity.' }
    return $matches[0]
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$scratch = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw "Scratch directory must be new: $scratch" }
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if ($TargetFramework -eq 'net48' -and -not $onWindows) { throw 'Physical Framework execution requires Windows.' }
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
New-Item -ItemType Directory -Path $scratch | Out-Null
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @(Get-ExactAudioPackage -Directory $feed)
if ($packages.Count -ne 1) { throw 'Expected exactly one Audio package.' }
$package = $packages[0]
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
    if ($entries.Count -ne 1) { throw 'Expected one Audio package manifest.' }
    $reader = [IO.StreamReader]::new($entries[0].Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $manifest.package.metadata.version
    if ([string] $manifest.package.metadata.id -cne 'Strands.Agents.Bidi.Audio' -or [string]::IsNullOrWhiteSpace($version)) { throw 'Unexpected package identity.' }
    if ([string] $manifest.package.metadata.license.InnerText -cne 'Apache-2.0' -or [string] $manifest.package.metadata.license.type -cne 'expression') { throw 'Expected Apache-2.0 package license.' }
    foreach ($target in @('netstandard2.0', 'net8.0', 'net10.0')) {
        foreach ($extension in @('dll', 'xml')) {
            if ($null -eq $archive.GetEntry("lib/$target/Strands.Agents.Bidi.Audio.$extension")) { throw "Missing $target $extension asset." }
        }
    }
    foreach ($asset in @('runtimes/win-x86/native/portaudio.dll', 'runtimes/win-x64/native/portaudio.dll', 'native-licenses/PortAudio.LICENSE.txt', 'buildTransitive/Strands.Agents.Bidi.Audio.targets')) {
        if ($null -eq $archive.GetEntry($asset)) { throw "Missing Audio package asset: $asset" }
    }
    foreach ($document in @('README.md', 'NOTICE', 'LICENSE.APACHE')) { if ($null -eq $archive.GetEntry($document)) { throw "Missing package document: $document" } }
    if (@($archive.Entries | Where-Object { $_.FullName -match '(?i)(^|/)(tests|testhost|xunit)(/|\.|-)' }).Count -ne 0) { throw 'Test binaries must not be packaged.' }
} finally { $archive.Dispose() }
$template = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Consumer.csproj.template')
$program = Join-Path $PSScriptRoot 'Program.cs.template'
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$nuget = "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"Strands.*`"/></packageSource><packageSource key=`"nuget`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>"

function Invoke-Headless([string] $File, [string[]] $Arguments, [string] $Log, [string] $WorkingDirectory, [hashtable] $Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    if ($onWindows) { $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden }
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $WorkingDirectory
    if ($null -ne $start.GetType().GetProperty('ArgumentList')) { foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) } }
    else {
        $quoted = @(foreach ($argument in $Arguments) {
            if ($argument.Contains('"')) { throw 'Quoted arguments are unsupported by the legacy Windows launcher.' }
            '"' + ($argument -replace '(\\+)$', '$1$1') + '"'
        })
        $start.Arguments = $quoted -join ' '
    }
    foreach ($name in @('ArtifactsPath', 'UseArtifactsOutput')) { $start.EnvironmentVariables.Remove($name) }
    foreach ($name in $Environment.Keys) { $start.EnvironmentVariables[$name] = [string] $Environment[$name] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Failed to launch $File" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if (Test-Path -LiteralPath $Log) { throw "Refusing to replace prior log: $Log" }
        [IO.File]::WriteAllText($Log, $text)
        if ($process.ExitCode -ne 0) { throw "Command failed ($($process.ExitCode)); retained log: $Log" }
        return $text
    } finally { $process.Dispose() }
}

$variants = @()
foreach ($target in @('net8.0', 'net10.0')) {
    if ($TargetFramework -eq 'all' -or $TargetFramework -eq $target) { $variants += [pscustomobject]@{ Name=$target; Target=$target; Platform='AnyCPU'; Pointer=[IntPtr]::Size } }
}
if (($TargetFramework -eq 'all' -and $onWindows) -or $TargetFramework -eq 'net48') {
    foreach ($platform in $FrameworkArchitectures) { $variants += [pscustomobject]@{ Name="framework-$platform"; Target='net48'; Platform=$platform; Pointer=$(if($platform -eq 'x64'){8}else{4}) } }
}
$receipts = @()
foreach ($variant in $variants) {
    $root = Join-Path $scratch $variant.Name
    $source = Join-Path $root 'src'
    $cache = Join-Path $root 'packages'
    $obj = (Join-Path $root 'obj') + [IO.Path]::DirectorySeparatorChar
    $output = Join-Path $root 'output'
    $temporary = Join-Path $root 'tmp'
    New-Item -ItemType Directory -Path $source, $cache, $temporary | Out-Null
    $project = Join-Path $source 'Consumer.csproj'
    $text = $template.Replace('__VERSION__', [Security.SecurityElement]::Escape($version)).Replace('__TARGET_FRAMEWORK__', $variant.Target).Replace('__PLATFORM__', $variant.Platform)
    [IO.File]::WriteAllText($project, $text)
    Copy-Item -LiteralPath $program -Destination (Join-Path $source 'Program.cs')
    $config = Join-Path $source 'NuGet.Config'
    [IO.File]::WriteAllText($config, $nuget)
    $environment = @{ NUGET_PACKAGES=$cache; NUGET_HTTP_CACHE_PATH=(Join-Path $root 'http-cache'); DOTNET_CLI_HOME=(Join-Path $root 'dotnet-home'); TEMP=$temporary; TMP=$temporary; TMPDIR=$temporary; MSBUILDDISABLENODEREUSE='1'; DOTNET_CLI_USE_MSBUILD_SERVER='0'; DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_NOLOGO='1'; DOTNET_CLI_UI_LANGUAGE='en-US' }
    $properties = @("-p:BaseIntermediateOutputPath=$obj")
    if ($variant.Target -eq 'net48') { $properties += @("-p:PlatformTarget=$($variant.Platform)") }
    $null = Invoke-Headless $dotnet (@('restore', $project, '--configfile', $config, '--force', '--no-cache', '--disable-build-servers', '-nr:false', '-m:1') + $properties) (Join-Path $root 'restore.log') $source $environment
    $build = Invoke-Headless $dotnet (@('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-nr:false', '-m:1', '-p:UseSharedCompilation=false', '--output', $output) + $properties) (Join-Path $root 'build.log') $source $environment
    if ($build -notmatch '0 Warning\(s\)' -or $build -match '(?m): warning ') { throw "Consumer build must have zero warnings: $root" }
    # Explicit known path also finds hidden .nupkg.metadata on Unix.
    $installed = Join-Path (Join-Path $cache 'strands.agents.bidi.audio') $version
    $metadata = Get-Content -Raw -LiteralPath (Join-Path $installed '.nupkg.metadata') | ConvertFrom-Json
    $comparison = if ($onWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$metadata.source).TrimEnd([IO.Path]::DirectorySeparatorChar), $feed.TrimEnd([IO.Path]::DirectorySeparatorChar), $comparison)) { throw 'Audio package did not come from the local feed.' }
    $cachedPackage = Join-Path $installed ("strands.agents.bidi.audio.$version.nupkg")
    if ((Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash) { throw 'Restored Audio package differs from the feed.' }
    $consumer = Join-Path $output $(if ($variant.Target -eq 'net48') { 'Strands.Bidi.Audio.Tests.exe' } else { 'Strands.Bidi.Audio.Tests.dll' })
    if ($variant.Target -eq 'net48') { $result = Invoke-Headless $consumer @([string]$variant.Pointer) (Join-Path $root 'consumer.log') $source $environment }
    else { $result = Invoke-Headless $dotnet @($consumer, [string]$variant.Pointer) (Join-Path $root 'consumer.log') $source $environment }
    $marker = "Audio package: native=ok; borrowed=true; pointer=$($variant.Pointer); asset=$(if ($variant.Target -eq 'net48') { 'netstandard2.0' } else { $variant.Target }); hardware=false"
    if (@($result -split '\r?\n' | Where-Object { $_ -ceq $marker }).Count -ne 1) { throw "Missing native consumer marker: $root" }
    if ($variant.Target -ne 'net48') {
        $published = Join-Path $root 'publish'
        $null = Invoke-Headless $dotnet (@('publish', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-nr:false', '-m:1', '-p:UseSharedCompilation=false', '-p:UseAppHost=false', '--output', $published) + $properties) (Join-Path $root 'publish.log') $source $environment
        $publishedResult = Invoke-Headless $dotnet @((Join-Path $published 'Strands.Bidi.Audio.Tests.dll'), [string]$variant.Pointer) (Join-Path $root 'published-consumer.log') $source $environment
        if (@($publishedResult -split '\r?\n' | Where-Object { $_ -ceq $marker }).Count -ne 1) { throw 'Published Audio package consumer failed.' }
    }
    Write-Output $marker
    $receipts += [pscustomobject]@{ Target=$variant.Target; Platform=$variant.Platform; Pointer=$variant.Pointer; BuildWarnings=0; Executable=$consumer; ExecutableSHA256=(Get-FileHash -LiteralPath $consumer -Algorithm SHA256).Hash; InstalledPackageMetadata=$metadata; Marker=$marker }
}
if ($receipts.Count -eq 0) { throw 'No applicable consumers were selected.' }
[ordered]@{ Status='AUDIO_PACKAGE_CONSUMER_PASS'; Package=$package.FullName; PackageSHA256=(Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash; Version=$version; Consumers=$receipts; Scope='Real package/native version and borrowed Audio construction/disposal only; no device initialization or hardware/native-latency proof' } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'summary.json') -Encoding utf8
Write-Output "Audio package proof passed; retained scratch: $scratch"
