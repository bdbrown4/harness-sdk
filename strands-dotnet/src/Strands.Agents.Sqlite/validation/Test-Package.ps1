<#
.SYNOPSIS
Builds fresh NuGet-only consumers and runs independent SQLite writer/reader processes.
.EXAMPLE
./Test-Package.ps1 -PackageDirectory ./feed -ScratchDirectory ./sqlite-package-proof
.NOTES
Windows all selects net8.0/net10.0 and physical Framework x64/x86. Unix all selects
the two modern runtimes. Native files are copied by the ordinary SDK/bundle targets.
The helper does not install a provider, alter native search paths, or use shadow copying.
Each command retains separate exact .stdout.bin/.stderr.bin channels. Its .log is a
UTF-8 text view (with an optional UTF-8 preamble recognized), stdout then stderr;
it does not claim total cross-stream ordering. Non-UTF8 error bytes remain in raw logs.
#>
param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [string] $ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('strands-sqlite-' + [Guid]::NewGuid().ToString('N'))),
    [ValidateSet('all', 'net8.0', 'net10.0', 'net48')][string] $TargetFramework = 'all',
    [ValidateSet('x64', 'x86')][string[]] $FrameworkArchitectures = @('x64', 'x86')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if ($TargetFramework -eq 'net48' -and -not $onWindows) { throw 'Physical Framework execution requires Windows.' }
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$scratch = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw "Scratch directory must be new: $scratch" }
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256([string] $Path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '') }
    finally { $hash.Dispose() }
}

$packages = @(Get-ChildItem -LiteralPath $feed -Filter 'Strands.Agents.Sqlite.*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one SQLite package.' }
$package = $packages[0]
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
    if ($entries.Count -ne 1) { throw 'Expected one SQLite package manifest.' }
    $reader = [IO.StreamReader]::new($entries[0].Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $manifest.package.metadata.version
    if ([string] $manifest.package.metadata.id -cne 'Strands.Agents.Sqlite' -or [string]::IsNullOrWhiteSpace($version)) { throw 'Unexpected package identity.' }
    if ([string] $manifest.package.metadata.license.InnerText -cne 'Apache-2.0' -or [string] $manifest.package.metadata.license.type -cne 'expression') { throw 'Expected Apache-2.0 package license.' }
    foreach ($target in @('netstandard2.0', 'net8.0', 'net10.0')) {
        foreach ($extension in @('dll', 'xml')) {
            if ($null -eq $archive.GetEntry("lib/$target/Strands.Agents.Sqlite.$extension")) { throw "Missing $target $extension asset." }
        }
    }
    foreach ($document in @('README.md', 'NOTICE', 'LICENSE.APACHE')) {
        if ($null -eq $archive.GetEntry($document)) { throw "Missing package document: $document" }
    }
    if (@($archive.Entries | Where-Object { $_.FullName -match '(?i)(^|/)(tests|testhost|xunit)(/|\.|-)' }).Count -ne 0) { throw 'Test binaries must not be packaged.' }
} finally { $archive.Dispose() }

New-Item -ItemType Directory -Path $scratch | Out-Null
[IO.File]::WriteAllText((Join-Path $scratch 'Directory.Build.props'), '<Project />')
[IO.File]::WriteAllText((Join-Path $scratch 'Directory.Build.targets'), '<Project />')
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Consumer.csproj.template'))
$program = Join-Path $PSScriptRoot 'Program.cs.template'
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$nuget = "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"Strands.*`"/></packageSource><packageSource key=`"nuget`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>"

function Get-Utf8View([byte[]] $Bytes) {
    $view = [IO.MemoryStream]::new($Bytes, $false)
    $reader = [IO.StreamReader]::new($view, [Text.UTF8Encoding]::new($true, $false), $false)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Invoke-Headless([string] $File, [string[]] $Arguments, [string] $Log, [string] $WorkingDirectory, [hashtable] $Environment) {
    if (Test-Path -LiteralPath $Log) { throw "Refusing to replace prior log: $Log" }
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
    $stdout = [IO.MemoryStream]::new()
    $stderr = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { throw "Failed to launch $File" }
        $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $process.WaitForExit()
        try { [void][Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutCopy, $stderrCopy)).GetAwaiter().GetResult() }
        finally {
            $stdoutBytes = $stdout.ToArray()
            $stderrBytes = $stderr.ToArray()
            [IO.File]::WriteAllBytes($Log + '.stdout.bin', $stdoutBytes)
            [IO.File]::WriteAllBytes($Log + '.stderr.bin', $stderrBytes)
            $text = (Get-Utf8View $stdoutBytes) + (Get-Utf8View $stderrBytes)
            [IO.File]::WriteAllText($Log, $text)
        }
        if ($process.ExitCode -ne 0) { throw "Command failed ($($process.ExitCode)); retained log: $Log" }
        return $text
    } finally { $process.Dispose(); $stdout.Dispose(); $stderr.Dispose() }
}

function Assert-InstalledPackage([string] $Name, [string] $Cache, [string] $Output, [string] $AssetTarget) {
    $packageRoot = Join-Path $Cache $Name.ToLowerInvariant()
    $versions = @(Get-ChildItem -LiteralPath $packageRoot -Directory)
    if ($versions.Count -ne 1) { throw "Expected one restored $Name version." }
    $installed = $versions[0].FullName
    $metadata = [IO.File]::ReadAllText((Join-Path $installed '.nupkg.metadata')) | ConvertFrom-Json
    $comparison = if ($onWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$metadata.source).TrimEnd([IO.Path]::DirectorySeparatorChar), $feed.TrimEnd([IO.Path]::DirectorySeparatorChar), $comparison)) { throw 'A Strands package did not come from the local feed.' }
    $filename = "$($Name.ToLowerInvariant()).$($versions[0].Name).nupkg"
    $matches = @(Get-ChildItem -LiteralPath $feed -File | Where-Object { $_.Name.Equals($filename, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -ne 1) { throw "Expected one matching local $Name package." }
    $cachedPackage = Join-Path $installed $filename
    if ((Get-Sha256 $cachedPackage) -cne (Get-Sha256 $matches[0].FullName)) { throw 'Restored package differs from the feed.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($cachedPackage)
    try {
        $entry = $zip.GetEntry("lib/$AssetTarget/$Name.dll")
        if ($null -eq $entry) { throw "Missing selected package asset for $Name." }
        $stream = $entry.Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $expectedHash = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
        finally { $hash.Dispose(); $stream.Dispose() }
        if ($expectedHash -cne (Get-Sha256 (Join-Path $Output "$Name.dll"))) { throw 'Consumer assembly differs from its selected package asset.' }
    } finally { $zip.Dispose() }
    return [pscustomobject]@{ Name=$Name; Version=$versions[0].Name; Source=$metadata.source; PackageSHA256=(Get-Sha256 $cachedPackage); SelectedAsset="lib/$AssetTarget/$Name.dll"; AssemblySHA256=$expectedHash }
}

$variants = @()
foreach ($target in @('net8.0', 'net10.0')) {
    if ($TargetFramework -eq 'all' -or $TargetFramework -eq $target) { $variants += [pscustomobject]@{ Name=$target; Target=$target; Platform='AnyCPU'; Pointer=0 } }
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
    $storage = Join-Path $root 'storage'
    New-Item -ItemType Directory -Path $source, $cache, $temporary | Out-Null
    $project = Join-Path $source 'Consumer.csproj'
    $text = $template.Replace('__VERSION__', [Security.SecurityElement]::Escape($version)).Replace('__TARGET_FRAMEWORK__', $variant.Target).Replace('__PLATFORM__', $variant.Platform)
    [IO.File]::WriteAllText($project, $text)
    Copy-Item -LiteralPath $program -Destination (Join-Path $source 'Program.cs')
    $config = Join-Path $source 'NuGet.Config'
    [IO.File]::WriteAllText($config, $nuget)
    $environment = @{ NUGET_PACKAGES=$cache; NUGET_HTTP_CACHE_PATH=(Join-Path $root 'http-cache'); DOTNET_CLI_HOME=(Join-Path $root 'dotnet-home'); TEMP=$temporary; TMP=$temporary; TMPDIR=$temporary; MSBUILDDISABLENODEREUSE='1'; DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_NOLOGO='1'; DOTNET_CLI_UI_LANGUAGE='en-US' }
    $properties = @("-p:BaseIntermediateOutputPath=$obj")
    $null = Invoke-Headless $dotnet (@('restore', $project, '--configfile', $config, '--force', '--no-cache', '--disable-build-servers') + $properties) (Join-Path $root 'restore.log') $source $environment
    $build = Invoke-Headless $dotnet (@('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-nr:false', '-p:UseSharedCompilation=false', '--output', $output) + $properties) (Join-Path $root 'build.log') $source $environment
    if ($build -notmatch '0 Warning\(s\)' -or $build -match '(?m): warning ') { throw "Consumer build must have zero warnings: $root" }
    $assetTarget = if ($variant.Target -eq 'net48') { 'netstandard2.0' } else { $variant.Target }
    $installed = @(Assert-InstalledPackage 'Strands.Agents.Sqlite' $cache $output $assetTarget; Assert-InstalledPackage 'Strands.Agents' $cache $output $assetTarget)
    $consumer = Join-Path $output $(if ($variant.Target -eq 'net48') { 'Consumer.exe' } else { 'Consumer.dll' })
    $runs = @()
    foreach ($mode in @('write', 'read')) {
        $log = Join-Path $root "$mode.log"
        if ($variant.Target -eq 'net48') { $result = Invoke-Headless $consumer @($mode, $storage, [string]$variant.Pointer) $log $source $environment }
        else { $result = Invoke-Headless $dotnet @($consumer, $mode, $storage, [string]$variant.Pointer) $log $source $environment }
        $markers = @($result -split '\r?\n' | Where-Object { $_.StartsWith('STRANDS_SQLITE_CONSUMER_JSON:', [StringComparison]::Ordinal) })
        if ($markers.Count -ne 1) { throw 'Expected one SQLite consumer result.' }
        $record = $markers[0].Substring('STRANDS_SQLITE_CONSUMER_JSON:'.Length) | ConvertFrom-Json
        $observedPointer = switch ([string]$record.Architecture) { 'X64' {8} 'Arm64' {8} 'X86' {4} 'Arm' {4} default {throw 'Unknown consumer architecture.'} }
        if ([string]$record.Mode -cne $mode -or $record.PointerSize -ne $observedPointer -or ($variant.Pointer -ne 0 -and $record.PointerSize -ne $variant.Pointer)) { throw 'Consumer process architecture/result mismatch.' }
        foreach ($property in @('Fts5', 'Persistence', 'NamespacedMemory', 'OpaqueBytes', 'BorrowedStorage')) { if ($record.$property -ne $true) { throw "Missing consumer capability result: $property" } }
        if ([string]::IsNullOrWhiteSpace([string]$record.SQLiteVersion) -or $record.HigherRankScore -le $record.LowerRankScore -or $record.LowerRankScore -le 0 -or $record.HigherRankScore -gt 1) { throw 'Missing native version or BM25 ranking evidence.' }
        $nativePath = [IO.Path]::GetFullPath([string]$record.NativePath)
        $prefix = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $comparison = if ($onWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not $nativePath.StartsWith($prefix, $comparison) -or [string]$record.NativeSHA256 -cne (Get-Sha256 $nativePath)) { throw 'Native module is outside the SDK output or its hash changed.' }
        $runs += $record
        Write-Output "SQLite $($variant.Name) $($mode): pointer=$($record.PointerSize); SQLite=$($record.SQLiteVersion); FTS5/rank/namespace/persistence=ok"
    }
    $receipts += [pscustomobject]@{ Target=$variant.Target; Platform=$variant.Platform; RequestedPointer=$variant.Pointer; ObservedPointer=$runs[0].PointerSize; BuildWarnings=0; Executable=$consumer; ExecutableSHA256=(Get-Sha256 $consumer); InstalledPackages=$installed; Runs=$runs }
}
if ($receipts.Count -eq 0) { throw 'No applicable consumers were selected.' }
[ordered]@{ Status='SQLITE_PACKAGE_CONSUMER_PASS'; Package=$package.FullName; PackageSHA256=(Get-Sha256 $package.FullName); Version=$version; Consumers=$receipts; Scope='Ordinary NuGet-only SQLite FTS5 writer/reader processes; no testhost/shadowcopy/provider/global-native-path workaround' } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $scratch 'summary.json') -Encoding utf8
Write-Output "SQLite package proof passed; retained scratch: $scratch"
