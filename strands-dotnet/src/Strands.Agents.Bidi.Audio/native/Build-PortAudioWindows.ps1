[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$WorkingDirectory,
    [ValidateSet('x86', 'x64')][string]$Architecture = 'x86',
    [string]$VisualStudioDirectory,
    [string]$WindowsSdkVersion
)
$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$FilePath) {
    $stream = [IO.File]::OpenRead($FilePath); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
$sourceCommit = '147dd722548358763a8b649b3e4b41dfffbcfbb6'
$archiveSha = 'BA87724CBD6E2BB643FDCDAFC50B6BA43881734FA5D106A730DB75C5DD729A06'
$working = [IO.Path]::GetFullPath($WorkingDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $working) { throw 'Use a new, dedicated working directory for the native build.' }
if (-not $VisualStudioDirectory) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $VisualStudioDirectory = & $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $VisualStudioDirectory) { throw 'An existing MSVC x86/x64 toolchain is required; this script installs nothing.' }
$msvc = Get-ChildItem -LiteralPath (Join-Path $VisualStudioDirectory 'VC/Tools/MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
if (-not $WindowsSdkVersion) { $WindowsSdkVersion = (Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name }
$cmake = Join-Path $VisualStudioDirectory 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ninja = Join-Path $VisualStudioDirectory 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe'
$compiler = Join-Path $msvc.FullName "bin/Hostx64/$Architecture/cl.exe"
foreach ($tool in @($cmake, $ninja, $compiler)) { if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing existing native tool: $tool" } }
New-Item -ItemType Directory -Path $working | Out-Null
$archive = Join-Path $working 'portaudio.zip'
Invoke-WebRequest -UseBasicParsing -Uri "https://codeload.github.com/PortAudio/portaudio/zip/$sourceCommit" -OutFile $archive
if ((Get-Sha256 $archive) -ne $archiveSha) { throw 'The pinned PortAudio source archive hash does not match.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $working)
$source = Join-Path $working "portaudio-$sourceCommit"
$build = Join-Path $working 'obj'
$previousPath = $env:PATH; $previousInclude = $env:INCLUDE; $previousLib = $env:LIB
try {
    $env:PATH = "$(Split-Path -Parent $compiler);$(Join-Path $sdk "bin/$WindowsSdkVersion/$Architecture");$(Split-Path -Parent $ninja);$previousPath"
    $env:INCLUDE = (@((Join-Path $msvc.FullName 'include')) + @('ucrt', 'shared', 'um', 'winrt', 'cppwinrt' | ForEach-Object { Join-Path $sdk "Include/$WindowsSdkVersion/$_" })) -join ';'
    $env:LIB = (@((Join-Path $msvc.FullName "lib/$Architecture"), (Join-Path $sdk "Lib/$WindowsSdkVersion/ucrt/$Architecture"), (Join-Path $sdk "Lib/$WindowsSdkVersion/um/$Architecture"))) -join ';'
    & $cmake -S $source -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_C_COMPILER=$compiler" "-DCMAKE_CXX_COMPILER=$compiler" -DCMAKE_BUILD_TYPE=Release -DPA_BUILD_SHARED=ON -DPA_BUILD_STATIC=OFF -DPA_LIBNAME_ADD_SUFFIX=OFF -DPA_DLL_LINK_WITH_STATIC_RUNTIME=ON -DPA_USE_ASIO=OFF -DPA_USE_DS=OFF -DPA_USE_WDMKS=OFF -DPA_USE_WMME=ON -DPA_USE_WASAPI=ON -DPA_BUILD_TESTS=OFF -DPA_BUILD_EXAMPLES=OFF
    if ($LASTEXITCODE -ne 0) { throw 'PortAudio CMake configuration failed.' }
    & $cmake --build $build --target portaudio --parallel 2
    if ($LASTEXITCODE -ne 0) { throw "PortAudio $Architecture compilation failed." }
    $dll = Join-Path $build 'portaudio.dll'
    $bytes = [IO.File]::ReadAllBytes($dll); $pe = [BitConverter]::ToInt32($bytes, 60)
    $machine = if ($Architecture -eq 'x86') { 0x14c } else { 0x8664 }
    if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne $machine) { throw "The built native asset must be an actual $Architecture PE DLL." }
    $dumpbin = Join-Path (Split-Path -Parent $compiler) 'dumpbin.exe'
    $imports = & $dumpbin /dependents $dll
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect native DLL dependencies.' }
    if ($imports -match '(?i)(VCRUNTIME|MSVCP|MSVCR|api-ms-win-crt|ucrtbase)') { throw 'The native DLL must not require a separately installed VC/UCRT runtime.' }
    $native = Join-Path $output "runtimes/win-$Architecture/native"; $licenses = Join-Path $output 'licenses'
    New-Item -ItemType Directory -Path $native, $licenses -Force | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $native 'portaudio.dll')
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination (Join-Path $licenses 'PortAudio.LICENSE.txt')
    [ordered]@{ SourceCommit = $sourceCommit; SourceArchiveSHA256 = $archiveSha; NativeSHA256 = (Get-Sha256 $dll); Architecture = "win-$Architecture"; StaticCrt = $true; Compiler = $compiler; WindowsSdk = $WindowsSdkVersion; DeviceAccess = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output "portaudio-win-$Architecture-provenance.json") -Encoding UTF8
}
finally { $env:PATH = $previousPath; $env:INCLUDE = $previousInclude; $env:LIB = $previousLib }
# Keep the dedicated working directory available for review; the caller owns its cleanup.
