param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [string] $ScratchDirectory
)

$ErrorActionPreference = 'Stop'
function Assert-PeArchitecture([string] $Executable, [string] $ExpectedArchitecture) {
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($Executable)
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $machine = $reader.PEHeaders.CoffHeader.Machine.ToString()
            $flags = [int] $reader.PEHeaders.CorHeader.Flags
            $matches = if ($ExpectedArchitecture -eq 'X64') { $machine -eq 'Amd64' -and ($flags -band 2) -eq 0 }
                else { $machine -eq 'I386' -and ($flags -band 2) -ne 0 }
            if (-not $matches) { throw "Consumer PE metadata does not require $ExpectedArchitecture (machine=$machine, flags=$flags)." }
            Write-Output "Verified $ExpectedArchitecture consumer PE machine=$machine, flags=$flags."
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'Strands.Agents.Cedar.*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one Cedar NuGet package.' }
$archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $nuspec = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
    if ($nuspec.Count -ne 1) { throw 'Expected exactly one Cedar nuspec.' }
    $reader = [IO.StreamReader]::new($nuspec[0].Open())
    try { [xml] $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $metadata.package.metadata.version
    if (-not $version) { throw 'Cedar package has no version.' }
    foreach ($rid in @('win-x86', 'win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')) {
        $extension = if ($rid.StartsWith('win-')) { '.dll' } elseif ($rid.StartsWith('osx-')) { '.dylib' } else { '.so' }
        $prefix = if ($rid.StartsWith('win-')) { '' } else { 'lib' }
        foreach ($library in @('engine', 'schema')) {
            if (-not $archive.GetEntry("runtimes/$rid/native/${prefix}strands_cedar_$library$extension")) { throw "Missing native package asset for $rid/$library." }
        }
    }
    foreach ($tfm in @('netstandard2.0', 'net8.0', 'net10.0')) {
        if (-not $archive.GetEntry("lib/$tfm/Strands.Agents.Cedar.dll") -or -not $archive.GetEntry("lib/$tfm/Strands.Agents.Cedar.xml")) { throw "Missing managed package assets for $tfm." }
    }
    $notices = $archive.GetEntry('native-licenses/THIRD-PARTY-NOTICES.md')
    if (-not $notices) { throw 'Missing native dependency notices.' }
    $reader = [IO.StreamReader]::new($notices.Open())
    try { $noticeText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $licenseCount = 0
    foreach ($line in ($noticeText -split "`n")) {
        if ($line.StartsWith('Original license files: ')) {
            foreach ($license in ($line.Substring('Original license files: '.Length).Trim() -split ', ')) {
                if (-not $archive.GetEntry('native-licenses/' + $license)) { throw "Missing original native dependency license at its declared package path: $license." }
                $licenseCount++
            }
        }
    }
    if ($licenseCount -eq 0) { throw 'Native dependency notices declare no original license files.' }
    Write-Output "Verified $licenseCount original native dependency license entries."
}
finally { $archive.Dispose() }

if (-not $ScratchDirectory) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('strands-cedar-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$ScratchDirectory = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $ScratchDirectory) { throw 'The Cedar consumer scratch directory must be new.' }
$consumer = Join-Path $ScratchDirectory 'consumer'
$cache = Join-Path $ScratchDirectory 'packages'
New-Item -ItemType Directory -Path $consumer, $cache -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $ScratchDirectory 'Directory.Build.props'), '<Project />')
[IO.File]::WriteAllText((Join-Path $ScratchDirectory 'Directory.Build.targets'), '<Project />')
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'package-consumer/Consumer.csproj.template'))
[IO.File]::WriteAllText((Join-Path $consumer 'Consumer.csproj'), $template.Replace('__VERSION__', [Security.SecurityElement]::Escape($version)))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-consumer/Program.cs') -Destination (Join-Path $consumer 'Program.cs')
$feed = [Security.SecurityElement]::Escape($PackageDirectory)
$config = Join-Path $ScratchDirectory 'NuGet.Config'
[IO.File]::WriteAllText($config, "<configuration><packageSources><clear /><add key=`"local`" value=`"$feed`" /><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" /></packageSources></configuration>")
$project = Join-Path $consumer 'Consumer.csproj'
& dotnet restore $project --packages $cache --configfile $config
if ($LASTEXITCODE -ne 0) { throw 'Isolated Cedar package restore failed.' }
& dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Cedar package consumer build failed.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
foreach ($tfm in @('net8.0', 'net10.0')) {
    $output = & dotnet run --project $project -c Release -f $tfm --no-build -- $architecture
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'Cedar:allow,deny,schema-rejected;Architecture:') { throw "Cedar package consumer failed for $tfm." }
    $output | Write-Output
}
if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    $legacy64Executable = Join-Path $consumer 'bin/Release/net48/Consumer.exe'
    Assert-PeArchitecture $legacy64Executable X64
    $output = & $legacy64Executable X64
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'Architecture:X64;PointerSize:8') { throw 'Cedar Framework x64 package consumer failed.' }
    $output | Write-Output
    $legacy32 = Join-Path $ScratchDirectory 'framework-x86'
    # Architecture-specific projects must not reuse an AnyCPU/x64 compile cache.
    $legacy32Source = Join-Path $ScratchDirectory 'consumer-x86'
    New-Item -ItemType Directory -Path $legacy32Source -Force | Out-Null
    $legacy32Project = Join-Path $legacy32Source 'Consumer.csproj'
    [IO.File]::WriteAllText($legacy32Project, $template.Replace('__VERSION__', [Security.SecurityElement]::Escape($version)).Replace('net48;net8.0;net10.0', 'net48'))
    Copy-Item -LiteralPath (Join-Path $consumer 'Program.cs') -Destination (Join-Path $legacy32Source 'Program.cs')
    & dotnet restore $legacy32Project --packages $cache --configfile $config -p:PlatformTarget=x86
    if ($LASTEXITCODE -ne 0) { throw 'Cedar Framework x86 package consumer restore failed.' }
    & dotnet build $legacy32Project -c Release -f net48 --no-restore -p:PlatformTarget=x86 -o $legacy32
    if ($LASTEXITCODE -ne 0) { throw 'Cedar Framework x86 package consumer build failed.' }
    Assert-PeArchitecture $legacy64Executable X64
    Assert-PeArchitecture (Join-Path $legacy32 'Consumer.exe') X86
    $output = & $legacy64Executable X64
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'Architecture:X64;PointerSize:8') { throw 'Cedar Framework x64 package consumer was not preserved.' }
    $output | Write-Output
    $output = & (Join-Path $legacy32 'Consumer.exe') X86
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'Architecture:X86;PointerSize:4') { throw 'Cedar Framework x86 package consumer failed.' }
    $output | Write-Output
}
Write-Output "Retained independent Cedar package consumer at $ScratchDirectory"
