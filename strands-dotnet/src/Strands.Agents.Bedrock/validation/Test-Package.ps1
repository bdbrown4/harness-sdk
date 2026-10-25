param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [string] $ScratchDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('strands-bedrock-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
)
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
$packages = @(Get-ChildItem -LiteralPath $feed -Filter 'Strands.Agents.Bedrock.*.nupkg')
if ($packages.Count -ne 1) { throw 'Expected exactly one Bedrock package.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $nuspecs = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
    if ($nuspecs.Count -ne 1) { throw 'Expected one package manifest.' }
    $reader = [IO.StreamReader]::new($nuspecs[0].Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $manifest.package.metadata.version
    if ([string] $manifest.package.metadata.id -cne 'Strands.Agents.Bedrock' -or [string]::IsNullOrWhiteSpace($version)) { throw 'Unexpected package identity.' }
    foreach ($target in @('netstandard2.0', 'net8.0', 'net10.0')) {
        foreach ($extension in @('dll', 'xml')) {
            if ($null -eq $archive.GetEntry("lib/$target/Strands.Agents.Bedrock.$extension")) { throw "Missing $target $extension asset." }
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
$escapedFeed = [Security.SecurityElement]::Escape($feed)
Set-Content -LiteralPath (Join-Path $scratch 'NuGet.Config') -Value "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"Strands.*`"/></packageSource><packageSource key=`"nuget`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" -Encoding utf8
$path = Join-Path $consumer 'Consumer.csproj'
& dotnet restore $path --configfile (Join-Path $scratch 'NuGet.Config') --packages $cache
if ($LASTEXITCODE -ne 0) { throw "Consumer restore failed ($LASTEXITCODE)." }
& dotnet build $path -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Consumer build failed ($LASTEXITCODE)." }
foreach ($target in @('net8.0', 'net10.0')) {
    $output = @(& dotnet run --project $path -c Release -f $target --no-build --no-restore 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Consumer $target failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)" }
    $output | Write-Output
    if (($output -join "`n") -notmatch 'Bedrock package: agent=complete; tool=item:7; calls=2; borrowed=true; cancellation=ok; pointer=') { throw "Unexpected $target output." }
}
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    foreach ($platform in @('x64', 'x86')) {
        $destination = Join-Path $scratch "framework-$platform"
        $platformSource = Join-Path $scratch "consumer-$platform"
        New-Item -ItemType Directory -Path $platformSource | Out-Null
        Copy-Item -LiteralPath $path -Destination (Join-Path $platformSource 'Consumer.csproj')
        Copy-Item -LiteralPath (Join-Path $consumer 'Program.cs') -Destination (Join-Path $platformSource 'Program.cs')
        $platformProject = Join-Path $platformSource 'Consumer.csproj'
        & dotnet restore $platformProject --configfile (Join-Path $scratch 'NuGet.Config') --packages $cache -p:TargetFrameworks=net48 "-p:PlatformTarget=$platform"
        if ($LASTEXITCODE -ne 0) { throw "Consumer net48 $platform restore failed ($LASTEXITCODE)." }
        & dotnet build $platformProject -c Release -f net48 --no-restore -p:TargetFrameworks=net48 "-p:PlatformTarget=$platform" --output $destination
        if ($LASTEXITCODE -ne 0) { throw "Consumer net48 $platform build failed ($LASTEXITCODE)." }
        $pointerSize = if ($platform -eq 'x64') { 8 } else { 4 }
        $output = @(& (Join-Path $destination 'Consumer.exe') $pointerSize 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Consumer net48 $platform failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)" }
        $output | Write-Output
        if ($output -notcontains "Bedrock package: agent=complete; tool=item:7; calls=2; borrowed=true; cancellation=ok; pointer=$pointerSize") { throw "Unexpected net48 $platform output." }
    }
}
Write-Output "Bedrock package proof passed; retained scratch: $scratch"
