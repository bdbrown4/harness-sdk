[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('x86', 'x64')][string] $Architecture,
    [Parameter(Mandatory = $true)][string] $WorkingDirectory,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../../../../artifacts/nova-native'),
    [string] $VisualStudioDirectory,
    [string] $WindowsSdkVersion
)
$ErrorActionPreference = 'Stop'
function Get-Sha256([string] $FilePath) {
    $stream = [IO.File]::OpenRead($FilePath)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Copy-IdenticalOrNew([string] $Source, [string] $Destination) {
    if (Test-Path -LiteralPath $Destination) {
        if ((Get-Sha256 $Source) -cne (Get-Sha256 $Destination)) { throw "Output source/license provenance conflicts: $Destination" }
        return
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
}
$working = [IO.Path]::GetFullPath($WorkingDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $working) { throw 'Use a fresh, dedicated working directory for each native architecture.' }
$native = Join-Path $output "runtimes/win-$Architecture/native/strands-nova-crt.dll"
if (Test-Path -LiteralPath $native) { throw "Refusing to overwrite an existing native asset: $native" }
if (-not $VisualStudioDirectory) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $locator -PathType Leaf)) { throw 'Existing Visual Studio C++ build tools are required; this script installs nothing.' }
    $VisualStudioDirectory = & $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $VisualStudioDirectory) { throw 'An existing MSVC x86/x64 toolchain is required.' }
$msvc = Get-ChildItem -LiteralPath (Join-Path $VisualStudioDirectory 'VC/Tools/MSVC') -Directory | Sort-Object { [version] $_.Name } -Descending | Select-Object -First 1
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
if (-not $WindowsSdkVersion) { $WindowsSdkVersion = (Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version] $_.Name } -Descending | Select-Object -First 1).Name }
$cmake = Join-Path $VisualStudioDirectory 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ninja = Join-Path $VisualStudioDirectory 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe'
$compiler = Join-Path $msvc.FullName "bin/Hostx64/$Architecture/cl.exe"
$dumpbin = Join-Path (Split-Path -Parent $compiler) 'dumpbin.exe'
$tar = (Get-Command tar -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
foreach ($tool in @($cmake, $ninja, $compiler, $dumpbin, $tar)) { if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing existing native tool: $tool" } }
$pinFile = Join-Path $PSScriptRoot 'source-pins.json'
$pins = ConvertFrom-Json -InputObject (Get-Content -Raw -LiteralPath $pinFile)
$components = @('aws-c-common', 'aws-c-cal', 'aws-c-io', 'aws-c-sdkutils', 'aws-c-compression', 'aws-c-http', 'aws-c-auth', 'aws-checksums')
if ($pins.Count -ne $components.Count -or @(Compare-Object ($components | Sort-Object) ($pins.Name | Sort-Object)).Count -ne 0) { throw 'Expected exactly the eight pinned AWS CRT components.' }
New-Item -ItemType Directory -Path $working | Out-Null
$source = Join-Path $working 'src'
New-Item -ItemType Directory -Path $source, (Join-Path $source 'source') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CMakeLists.txt') -Destination $source
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native') -Destination $source -Recurse
foreach ($pin in $pins) {
    if ($pin.Commit -notmatch '^[0-9a-f]{40}$' -or $pin.ArchiveSHA256 -notmatch '^[0-9A-F]{64}$' -or
        $pin.URL -cne "https://codeload.github.com/awslabs/$($pin.Name)/tar.gz/$($pin.Commit)") { throw 'Invalid source pin identity.' }
    $archive = Join-Path $working ($pin.Name + '.tar.gz')
    Invoke-WebRequest -UseBasicParsing -Uri $pin.URL -OutFile $archive
    if ((Get-Item -LiteralPath $archive).Length -ne $pin.ArchiveBytes -or (Get-Sha256 $archive) -cne $pin.ArchiveSHA256) { throw "Source archive does not match its pin: $($pin.Name)" }
    $component = Join-Path $source "source/$($pin.Name)"
    New-Item -ItemType Directory -Path $component | Out-Null
    & $tar -xzf $archive -C $component --strip-components=1
    if ($LASTEXITCODE -ne 0) { throw "Cannot extract the pinned source: $($pin.Name)" }
    foreach ($license in Get-ChildItem -LiteralPath $component -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-LICENSES)(\..*)?$' }) {
        Copy-IdenticalOrNew $license.FullName (Join-Path $output "licenses/aws-crt-native/$($pin.Name)/$($license.Name)")
    }
}
$previousPath = $env:PATH; $previousInclude = $env:INCLUDE; $previousLib = $env:LIB
try {
    $env:PATH = "$(Split-Path -Parent $compiler);$(Join-Path $sdk "bin/$WindowsSdkVersion/$Architecture");$(Split-Path -Parent $ninja);$previousPath"
    $env:INCLUDE = (@((Join-Path $msvc.FullName 'include')) + @('ucrt', 'shared', 'um', 'winrt', 'cppwinrt' | ForEach-Object { Join-Path $sdk "Include/$WindowsSdkVersion/$_" })) -join ';'
    $env:LIB = @((Join-Path $msvc.FullName "lib/$Architecture"), (Join-Path $sdk "Lib/$WindowsSdkVersion/ucrt/$Architecture"), (Join-Path $sdk "Lib/$WindowsSdkVersion/um/$Architecture")) -join ';'
    $build = Join-Path $working 'obj'
    & $cmake -S $source -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_C_COMPILER=$compiler" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_DEFAULT_CMP0141=NEW
    if ($LASTEXITCODE -ne 0) { throw 'Nova transport CMake configuration failed.' }
    & $cmake --build $build --target strands-nova-crt --parallel 2
    if ($LASTEXITCODE -ne 0) { throw "Nova transport $Architecture compilation failed." }
    $dll = Join-Path $build 'strands-nova-crt.dll'
    $bytes = [IO.File]::ReadAllBytes($dll); $pe = [BitConverter]::ToInt32($bytes, 60)
    $machine = if ($Architecture -eq 'x86') { 0x14c } else { 0x8664 }
    if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne $machine) { throw 'Native DLL PE architecture does not match its requested RID.' }
    $imports = & $dumpbin /dependents $dll
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect native dependencies.' }
    if ($imports -match '(?i)(VCRUNTIME|MSVCP|MSVCR|api-ms-win-crt|ucrtbase)') { throw 'The DLL must use static CRT without a VC/UCRT installation dependency.' }
    $exportLines = & $dumpbin /exports $dll
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect native exports.' }
    $exports = @(foreach ($line in $exportLines) {
        $exportMatch = [regex]::Match($line, '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+([A-Za-z_]\w*)')
        if ($exportMatch.Success) { $exportMatch.Groups[1].Value }
    })
    $expected = @('strands_crt_abi_version', 'strands_crt_error_name', 'strands_crt_http_cancel', 'strands_crt_http_join_and_destroy', 'strands_crt_http_open', 'strands_crt_http_pending_writes', 'strands_crt_http_update_window', 'strands_crt_http_write', 'strands_crt_sign_event', 'strands_crt_sign_request')
    if ($exports.Count -ne $expected.Count -or @(Compare-Object ($expected | Sort-Object) ($exports | Sort-Object)).Count -ne 0) { throw 'Native exports do not match the private Nova ABI.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $native) -Force | Out-Null
    Copy-Item -LiteralPath $dll -Destination $native
    Copy-IdenticalOrNew $pinFile (Join-Path $output 'source-pins.json')
    $bridge = @(foreach ($file in Get-ChildItem -LiteralPath (Join-Path $source 'native') -File) { [ordered]@{ Name=$file.Name; SHA256=(Get-Sha256 $file.FullName) } })
    [ordered]@{ Architecture="win-$Architecture"; NativeSHA256=(Get-Sha256 $native); SourcePinsSHA256=(Get-Sha256 $pinFile); CMakeSHA256=(Get-Sha256 (Join-Path $source 'CMakeLists.txt')); Bridge=$bridge; Compiler=$compiler; WindowsSdk=$WindowsSdkVersion; StaticCrt=$true; Exports=$exports; AWSRequests=$false } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output "nova-win-$Architecture-provenance.json") -Encoding UTF8
}
finally { $env:PATH = $previousPath; $env:INCLUDE = $previousInclude; $env:LIB = $previousLib }
# The caller owns the dedicated working directory and its cleanup after native children exit.
