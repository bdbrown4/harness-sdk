param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [string] $ScratchDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-PeArchitecture([string] $Executable, [string] $ExpectedArchitecture) {
    $stream = [IO.File]::OpenRead($Executable)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            $stream.Position = 0x3c
            $pe = $reader.ReadInt32()
            if ($pe -lt 0 -or $pe -gt $stream.Length - 24) { throw 'Invalid consumer DOS header.' }
            $stream.Position = $pe
            if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid consumer PE signature.' }
            $machine = $reader.ReadUInt16()
            $sections = $reader.ReadUInt16()
            $stream.Position = $pe + 20
            $optionalSize = $reader.ReadUInt16()
            $optional = $pe + 24
            $stream.Position = $optional
            $magic = $reader.ReadUInt16()
            $directories = if ($magic -eq 0x10b) { 96 } elseif ($magic -eq 0x20b) { 112 } else { throw 'Invalid consumer optional PE header.' }
            $stream.Position = $optional + $directories + (14 * 8)
            $cliRva = $reader.ReadUInt32()
            $cliOffset = $null
            for ($index = 0; $index -lt $sections; $index++) {
                $stream.Position = $optional + $optionalSize + ($index * 40) + 8
                $virtualSize = $reader.ReadUInt32()
                $virtualAddress = $reader.ReadUInt32()
                $rawSize = $reader.ReadUInt32()
                $rawOffset = $reader.ReadUInt32()
                if ($cliRva -ge $virtualAddress -and $cliRva -lt $virtualAddress + [Math]::Max($virtualSize, $rawSize)) { $cliOffset = $rawOffset + ($cliRva - $virtualAddress); break }
            }
            if ($null -eq $cliOffset) { throw 'Missing consumer CLR header.' }
            $stream.Position = $cliOffset + 16
            $flags = $reader.ReadUInt32()
            $matches = if ($ExpectedArchitecture -eq 'X64') { $machine -eq 0x8664 -and ($flags -band 2) -eq 0 }
                else { $machine -eq 0x14c -and ($flags -band 2) -ne 0 }
            if (-not $matches) { throw "Consumer PE metadata does not require $ExpectedArchitecture (machine=$machine, flags=$flags)." }
            Write-Output "Verified $ExpectedArchitecture consumer PE machine=$machine, flags=$flags."
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'Strands.Agents.Monty.*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one Monty NuGet package.' }
$archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $nuspec = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
    if ($nuspec.Count -ne 1) { throw 'Expected exactly one Monty nuspec.' }
    $reader = [IO.StreamReader]::new($nuspec[0].Open())
    try { [xml] $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string] $metadata.package.metadata.version
    if (-not $version) { throw 'Monty package has no version.' }
    foreach ($rid in @('win-x86', 'win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')) {
        $extension = if ($rid.StartsWith('win-')) { '.exe' } else { '' }
        if (-not $archive.GetEntry("runtimes/$rid/native/strands_monty_worker$extension")) { throw "Missing native package worker for $rid." }
    }
    foreach ($tfm in @('netstandard2.0', 'net8.0', 'net10.0')) {
        if (-not $archive.GetEntry("lib/$tfm/Strands.Agents.Monty.dll") -or -not $archive.GetEntry("lib/$tfm/Strands.Agents.Monty.xml")) { throw "Missing managed package assets for $tfm." }
    }
    $notices = $archive.GetEntry('licenses/THIRD-PARTY-NOTICES.md')
    if (-not $notices) { throw 'Missing native dependency notices.' }
    $reader = [IO.StreamReader]::new($notices.Open())
    try { $noticeText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $references = @([regex]::Matches($noticeText, '(?m)^(?:Original license files|Declared-license attribution files \(see SOURCE\.md\)): (.+)\r?$'))
    if ($references.Count -eq 0) { throw 'Native dependency notices contain no original source/license references.' }
    foreach ($reference in $references) {
        foreach ($path in $reference.Groups[1].Value.Trim().Split(',')) {
            $entry = 'licenses/' + $path.Trim()
            if (-not $archive.GetEntry($entry)) { throw "Missing documented native dependency license/source entry: $entry" }
        }
    }
}
finally { $archive.Dispose() }

if (-not $ScratchDirectory) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('strands-monty-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$ScratchDirectory = [IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $ScratchDirectory) { throw 'The Monty consumer scratch directory must be new.' }
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
if ($LASTEXITCODE -ne 0) { throw 'Isolated Monty package restore failed.' }
& dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Monty package consumer build failed.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
foreach ($tfm in @('net8.0', 'net10.0')) {
    $output = & dotnet run --project $project -c Release -f $tfm --no-build -- $architecture
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'Monty:real-python,host-tool,print-only;Engine:0.0.23;ABI:1;ProcessArchitecture:') { throw "Monty package consumer failed for $tfm." }
    $output | Write-Output
}
if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    $legacy64Executable = Join-Path $consumer 'bin/Release/net48/Consumer.exe'
    Assert-PeArchitecture $legacy64Executable X64
    $output = & $legacy64Executable X64
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'ProcessArchitecture:X64;HostPointerSize:8;WorkerPointerBits:64') { throw 'Monty Framework x64 package consumer failed.' }
    $output | Write-Output
    $legacy32 = Join-Path $ScratchDirectory 'framework-x86'
    # Architecture-specific projects must not reuse an AnyCPU/x64 compile cache.
    $legacy32Source = Join-Path $ScratchDirectory 'consumer-x86'
    New-Item -ItemType Directory -Path $legacy32Source -Force | Out-Null
    $legacy32Project = Join-Path $legacy32Source 'Consumer.csproj'
    [IO.File]::WriteAllText($legacy32Project, $template.Replace('__VERSION__', [Security.SecurityElement]::Escape($version)).Replace('net48;net8.0;net10.0', 'net48'))
    Copy-Item -LiteralPath (Join-Path $consumer 'Program.cs') -Destination (Join-Path $legacy32Source 'Program.cs')
    & dotnet restore $legacy32Project --packages $cache --configfile $config -p:PlatformTarget=x86
    if ($LASTEXITCODE -ne 0) { throw 'Monty Framework x86 package consumer restore failed.' }
    & dotnet build $legacy32Project -c Release -f net48 --no-restore -p:PlatformTarget=x86 -o $legacy32
    if ($LASTEXITCODE -ne 0) { throw 'Monty Framework x86 package consumer build failed.' }
    Assert-PeArchitecture $legacy64Executable X64
    Assert-PeArchitecture (Join-Path $legacy32 'Consumer.exe') X86
    $output = & $legacy64Executable X64
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'ProcessArchitecture:X64;HostPointerSize:8;WorkerPointerBits:64') { throw 'Monty Framework x64 package consumer was not preserved.' }
    $output | Write-Output
    $output = & (Join-Path $legacy32 'Consumer.exe') X86
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'ProcessArchitecture:X86;HostPointerSize:4;WorkerPointerBits:32') { throw 'Monty Framework x86 package consumer failed.' }
    $output | Write-Output
}
Write-Output "Retained independent Monty package consumer at $ScratchDirectory"
