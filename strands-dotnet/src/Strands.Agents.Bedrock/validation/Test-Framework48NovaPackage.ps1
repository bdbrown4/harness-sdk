param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [string] $ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('strands-nova-package-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This package consumer requires Windows Framework execution.' }
$onWindows = $true
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$scratch = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'Use a new, dedicated package validation directory.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
New-Item -ItemType Directory -Path $scratch | Out-Null
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$candidates = @(foreach ($file in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $specifications = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
        if ($specifications.Count -ne 1) { throw 'A candidate package must contain exactly one nuspec.' }
        $reader = [IO.StreamReader]::new($specifications[0].Open())
        try { [xml] $specification = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ([string] $specification.package.metadata.id -ieq 'Strands.Agents.Bedrock') { $file }
    } finally { $zip.Dispose() }
})
if ($candidates.Count -ne 1) { throw 'Expected exactly one Bedrock package by nuspec identity.' }
$package = $candidates[0]
$nativeAssets = @()
$zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entry = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })[0]
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml] $specification = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $specification.package.metadata.version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Missing package version.' }
    if ($null -eq $zip.GetEntry('lib/netstandard2.0/Strands.Agents.Bedrock.dll')) { throw 'Missing normal .NET Standard 2.0 managed asset.' }
    foreach ($architecture in @('x64', 'x86')) {
        $path = "runtimes/win-$architecture/native/strands-nova-crt.dll"
        $entry = $zip.GetEntry($path)
        if ($null -eq $entry) { throw "Missing real packaged native asset: $path" }
        $stream = $entry.Open(); $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        $nativeAssets += [pscustomobject]@{ Path=$path; SHA256=$hash }
    }
} finally { $zip.Dispose() }
$template = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Framework48NovaConsumer.csproj.template')
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$configuration = "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"Strands.*`"/></packageSource><packageSource key=`"nuget`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>"
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

$consumers = @()
foreach ($architecture in @('x64', 'x86')) {
    $root = Join-Path $scratch "consumers/nova/$architecture"
    # Keep dependency caches outside the project tree so SDK content globs cannot inspect every cached TFM.
    $isolation = Join-Path $scratch "isolation/nova/$architecture"
    $cache = Join-Path $isolation 'packages'; $temporary = Join-Path $isolation 'tmp'
    New-Item -ItemType Directory -Path $root, $cache, $temporary | Out-Null
    $project = Join-Path $root 'Framework48NovaConsumer.csproj'
    $text = $template.Replace('__VERSION__', [Security.SecurityElement]::Escape($version)).Replace('__PLATFORM__', $architecture)
    [IO.File]::WriteAllText($project, $text)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Framework48NovaConsumer.cs.template') -Destination (Join-Path $root 'Program.cs')
    [IO.File]::WriteAllText((Join-Path $root 'NuGet.Config'), $configuration)
    $environment = @{ NUGET_PACKAGES=$cache; NUGET_HTTP_CACHE_PATH=(Join-Path $isolation 'http-cache'); DOTNET_CLI_HOME=(Join-Path $isolation 'dotnet-home'); TEMP=$temporary; TMP=$temporary; TMPDIR=$temporary; MSBUILDDISABLENODEREUSE='1'; DOTNET_CLI_USE_MSBUILD_SERVER='0'; DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_NOLOGO='1'; DOTNET_CLI_UI_LANGUAGE='en-US' }
    $null = Invoke-Headless $dotnet @('restore', $project, '--configfile', (Join-Path $root 'NuGet.Config'), '--force', '--no-cache', '--disable-build-servers', '-nr:false', '-m:1') (Join-Path $root 'restore.log') $root $environment
    $build = Invoke-Headless $dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-nr:false', '-m:1', '-p:UseSharedCompilation=false') (Join-Path $root 'build.log') $root $environment
    if ($build -notmatch '0 Warning\(s\)' -or $build -match '(?m): warning ') { throw 'The native package consumer build must have zero warnings.' }
    $installed = Join-Path (Join-Path $cache 'strands.agents.bedrock') $version
    $metadata = Get-Content -Raw -LiteralPath (Join-Path $installed '.nupkg.metadata') | ConvertFrom-Json
    if (-not [string]::Equals([IO.Path]::GetFullPath([string] $metadata.source).TrimEnd([IO.Path]::DirectorySeparatorChar), $feed.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Sha256 (Join-Path $installed "strands.agents.bedrock.$version.nupkg")) -cne (Get-Sha256 $package.FullName)) { throw 'The consumer must use the exact candidate package from the fresh local feed.' }
    $output = Join-Path $root 'bin/Release/net48'
    foreach ($asset in $nativeAssets) {
        if ((Get-Sha256 (Join-Path $output $asset.Path)) -cne $asset.SHA256) { throw 'Copied native bytes differ from the candidate package.' }
    }
    $pointer = if ($architecture -eq 'x86') { 4 } else { 8 }
    $executable = Join-Path $output 'Framework48NovaConsumer.exe'
    $result = Invoke-Headless $executable @([string] $pointer) (Join-Path $root 'consumer.log') $root $environment
    $lines = @($result -split '\r?\n')
    $marker = "Nova package: pointer=$pointer; asset=netstandard2.0; abi=2; network=false"
    if (@($lines | Where-Object { $_ -ceq $marker }).Count -ne 1 -or @($lines | Where-Object { $_ -ceq 'PASS framework48-nova-native-abi-smoke' }).Count -ne 1) { throw 'The native consumer did not provide its required ABI/asset/architecture markers.' }
    Write-Output $result
    $consumers += [pscustomobject]@{ Architecture=$architecture; Pointer=$pointer; Executable=$executable; ExecutableSHA256=(Get-Sha256 $executable); Marker=$marker; BuildWarnings=0 }
}
[ordered]@{ Scope='Fresh NuGet Framework consumer construction/metadata, normal NS2 asset and real native ABI2 only; no Start, TLS, AWS or network proof'; Package=$package.FullName; PackageSHA256=(Get-Sha256 $package.FullName); NativeAssets=$nativeAssets; Consumers=$consumers } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $scratch 'summary.json') -Encoding utf8
