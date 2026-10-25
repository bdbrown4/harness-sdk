param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [string] $ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('strands-bidi-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
)
function Get-ExactBidiPackage([string] $Directory) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $matches = @(foreach ($candidate in Get-ChildItem -LiteralPath $Directory -Filter '*.nupkg' -File) {
        $zip = [IO.Compression.ZipFile]::OpenRead($candidate.FullName)
        try {
            $entries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
            if ($entries.Count -ne 1) { throw 'Expected one candidate package manifest.' }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { [xml] $specification = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ([string] $specification.package.metadata.id -ieq 'Strands.Agents.Bidi') { $candidate }
        } finally { $zip.Dispose() }
    })
    if ($matches.Count -ne 1) { throw 'Expected exactly one bidirectional package by nuspec identity.' }
    return $matches[0]
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$scratch = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw "Scratch directory must be new: $scratch" }
$consumer = Join-Path $scratch 'consumer'
$cache = Join-Path $scratch 'packages'
New-Item -ItemType Directory -Path $consumer, $cache | Out-Null
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $scratch 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
$packages = @(Get-ExactBidiPackage -Directory $feed)
if ($packages.Count -ne 1) { throw 'Expected exactly one bidirectional package.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $nuspecs = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
    if ($nuspecs.Count -ne 1) { throw 'Expected one package manifest.' }
    $reader = [IO.StreamReader]::new($nuspecs[0].Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $manifest.package.metadata.version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Missing package version.' }
    foreach ($target in @('netstandard2.0', 'net8.0', 'net10.0')) {
        foreach ($extension in @('dll', 'xml')) {
            if ($null -eq $archive.GetEntry("lib/$target/Strands.Agents.Bidi.$extension")) { throw "Missing $target $extension asset." }
        }
    }
    foreach ($document in @('README.md', 'NOTICE', 'LICENSE.APACHE')) {
        if ($null -eq $archive.GetEntry($document)) { throw "Missing package document: $document" }
    }
    if (@($archive.Entries | Where-Object { $_.FullName -match '(^|/)(tests|testhost)(/|\.)' }).Count -ne 0) { throw 'Test binaries must not be packaged.' }
} finally { $archive.Dispose() }
$project = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Consumer.csproj.template')).Replace('__VERSION__', [Security.SecurityElement]::Escape($version))
Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Value $project -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs.template') -Destination (Join-Path $consumer 'Program.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WebSocketServer.cs.template') -Destination (Join-Path $consumer 'WebSocketServer.cs')
$escapedFeed = [Security.SecurityElement]::Escape($feed)
Set-Content -LiteralPath (Join-Path $scratch 'NuGet.Config') -Value "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"Strands.*`"/></packageSource><packageSource key=`"nuget`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" -Encoding utf8
$path = Join-Path $consumer 'Consumer.csproj'
& dotnet restore $path --configfile (Join-Path $scratch 'NuGet.Config') --packages $cache
if ($LASTEXITCODE -ne 0) { throw "Consumer restore failed ($LASTEXITCODE)." }
& dotnet build $path -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Consumer build failed ($LASTEXITCODE)." }
$targets = @('net8.0', 'net10.0')
foreach ($target in $targets) {
    $output = @(& dotnet run --project $path -c Release -f $target --no-build --no-restore 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Consumer $target failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)" }
    $output | Write-Output
    if (($output -join "`n") -notmatch 'Bidi package: websocket=ok; tool=7; continuation=one; audio=ok; agent=7') { throw "Unexpected $target output." }
}
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    foreach ($architecture in @('x64', 'x86')) {
        $variant = Join-Path $scratch ("framework-" + $architecture)
        $source = Join-Path $variant 'source'
        $outputDirectory = Join-Path $variant 'output'
        New-Item -ItemType Directory -Path $source | Out-Null
        $frameworkProject = Join-Path $source 'Consumer.csproj'
        [IO.File]::WriteAllText($frameworkProject, $project.Replace('net48;net8.0;net10.0', 'net48'), [Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath (Join-Path $consumer 'Program.cs') -Destination (Join-Path $source 'Program.cs')
        Copy-Item -LiteralPath (Join-Path $consumer 'WebSocketServer.cs') -Destination (Join-Path $source 'WebSocketServer.cs')
        & dotnet restore $frameworkProject --configfile (Join-Path $scratch 'NuGet.Config') --packages $cache -p:PlatformTarget=$architecture
        if ($LASTEXITCODE -ne 0) { throw "Framework $architecture consumer restore failed." }
        & dotnet build $frameworkProject -c Release -f net48 --no-restore -p:PlatformTarget=$architecture -p:Prefer32Bit=false -o $outputDirectory
        if ($LASTEXITCODE -ne 0) { throw "Framework $architecture consumer build failed." }
        $pointer = if ($architecture -eq 'x86') { 4 } else { 8 }
        $executable = Join-Path $outputDirectory 'Consumer.exe'
        $output = @(& $executable $pointer 2>&1)
        $consumerExit = $LASTEXITCODE
        $output | Write-Output
        $architectureMarker = "Bidi package architecture: pointer=$pointer; asset=netstandard2.0"
        if ($consumerExit -ne 0 -or $output -cnotcontains 'Bidi package: websocket=ok; tool=7; continuation=one; audio=ok; agent=7' -or
            @($output | Where-Object { $_ -ceq $architectureMarker }).Count -ne 1) { throw "Framework $architecture package consumer failed." }
    }
}
Write-Output "Bidirectional package proof passed; retained scratch: $scratch"
