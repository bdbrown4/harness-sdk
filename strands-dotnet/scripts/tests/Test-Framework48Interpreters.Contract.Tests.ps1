param(
    [Parameter(Mandatory = $true)][string] $ProductionScript,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'These interpreter staging contracts require Windows filesystem links.' }
$ProductionScript = (Resolve-Path -LiteralPath $ProductionScript).Path
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new contract evidence directory.' }
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $evidence | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Get-ContractHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}
if (-not ('InterpreterAliasFixture' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InterpreterAliasFixture {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CreateSymbolicLink(string path, string target, uint flags);
}
'@
}
function New-Link([string] $Path, [string] $Target, [bool] $Directory = $false) {
    $flags = [uint32] 2
    if ($Directory) { $flags = $flags -bor 1 }
    if (-not [InterpreterAliasFixture]::CreateSymbolicLink($Path, $Target, $flags)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
}
function New-Fixture([string] $Name) {
    $case = Join-Path $evidence $Name
    $python = Join-Path $case 'runtime'; $git = Join-Path $case 'git'
    New-Item -ItemType Directory -Path (Join-Path $python 'Lib'), (Join-Path $git 'usr/bin'), (Join-Path $git 'usr/share/licenses') | Out-Null
    [IO.File]::WriteAllText((Join-Path $python 'python.exe'), 'selected-python-staging-fixture')
    [IO.File]::WriteAllText((Join-Path $python 'Lib/standard.py'), 'standard-library-staging-fixture')
    [IO.File]::WriteAllText((Join-Path $git 'usr/bin/bash.exe'), 'explicit-nonrunnable-bash-staging-fixture')
    [IO.File]::WriteAllText((Join-Path $git 'usr/share/licenses/license.txt'), 'license-staging-fixture')
    [IO.File]::WriteAllText((Join-Path $git 'LICENSE.txt'), 'git-license-staging-fixture')
    return @{ Root=$case; Python=$python; Executable=(Join-Path $python 'python.exe'); Git=$git; Output=(Join-Path $case 'staged') }
}
function Invoke-Stage($Fixture) { & $ProductionScript -PythonExecutable $Fixture.Executable -GitDirectory $Fixture.Git -OutputDirectory $Fixture.Output }
function Test-Case([string] $Name, [scriptblock] $Body, [bool] $NeedsLinks = $true) {
    if ($NeedsLinks -and -not $linkCapability.Available) {
        $results.Add([pscustomobject]@{ Name=$Name; Passed=$false; Skipped=$true; Error=$linkCapability.Error }); return
    }
    try { & $Body; $results.Add([pscustomobject]@{ Name=$Name; Passed=$true; Skipped=$false }) }
    catch { $results.Add([pscustomobject]@{ Name=$Name; Passed=$false; Skipped=$false; Error=$_.Exception.Message; Stack=$_.ScriptStackTrace }) }
}
$linkCapability = [ordered]@{ Available=$false; Error=$null; JunctionAvailable=$false; JunctionError=$null; NoElevationOrSettingsChange=$true }
try {
    $probe = Join-Path $evidence 'link-capability'; New-Item -ItemType Directory -Path $probe | Out-Null
    $target = Join-Path $probe 'target.exe'; [IO.File]::WriteAllText($target, 'link-capability-fixture')
    $alias = Join-Path $probe 'python3.exe'; New-Link $alias $target
    $item = Get-Item -LiteralPath $alias -Force
    $linkCapability.Available = $true
    $linkCapability.LinkType = [string] $item.LinkType; $linkCapability.Target = @($item.Target)
} catch { $linkCapability.Error = $_.Exception.Message }
try {
    $target = Join-Path $probe 'directory'; New-Item -ItemType Directory -Path $target | Out-Null
    $junction = Join-Path $probe 'junction'; New-Item -ItemType Junction -Path $junction -Value $target | Out-Null
    $linkCapability.JunctionAvailable = $true
} catch { $linkCapability.JunctionError = $_.Exception.Message }

Test-Case 'RegularPython3ExecutableIsCopiedAndVerified' {
    $f = New-Fixture 'regular-python3'
    [IO.File]::WriteAllText((Join-Path $f.Python 'python3.exe'), 'ordinary-distinct-python3-file')
    Invoke-Stage $f
    if ((Get-ContractHash (Join-Path $f.Python 'python3.exe')) -cne (Get-ContractHash (Join-Path $f.Output 'python/python3.exe'))) { throw 'A regular python3.exe was omitted or changed.' }
    & $ProductionScript -OutputDirectory $f.Output -VerifyOnly
} $false
if ($linkCapability.JunctionAvailable) {
    Test-Case 'Reject-DirectoryJunction' {
        $f = New-Fixture 'directory-junction'
        $target = Join-Path $f.Root 'outside-directory'; New-Item -ItemType Directory -Path $target | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $f.Python 'python3.exe') -Value $target | Out-Null
        $rejected = $false
        try { Invoke-Stage $f } catch {
            if ($_.Exception.Message -notlike '*reparse*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'An unsupported directory junction was accepted.' }
    } $false
} else { $results.Add([pscustomobject]@{ Name='Reject-DirectoryJunction'; Passed=$false; Skipped=$true; Error=$linkCapability.JunctionError }) }
Test-Case 'ExactTopLevelSelectedAliasIsOmittedAndRecorded' {
    $f = New-Fixture 'allowed-alias'; New-Link (Join-Path $f.Python 'python3.exe') $f.Executable
    $before = Get-ContractHash $f.Executable; Invoke-Stage $f
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $f.Output 'interpreter-manifest.json') | ConvertFrom-Json
    $omitted = @($manifest.OmittedPythonAliases)
    if ($omitted.Count -ne 1 -or $omitted[0].Source -cne (Join-Path $f.Python 'python3.exe') -or $omitted[0].Target -cne $f.Executable -or $omitted[0].SHA256 -cne $before) { throw 'The precise unused alias identity was not recorded.' }
    if (Test-Path -LiteralPath (Join-Path $f.Output 'python/python3.exe')) { throw 'The unused alias was copied.' }
    if ((Get-ContractHash (Join-Path $f.Output 'python/python.exe')) -cne $before -or (Get-ContractHash $f.Executable) -cne $before) { throw 'The selected interpreter or its copy changed.' }
    & $ProductionScript -OutputDirectory $f.Output -VerifyOnly
}
foreach ($variant in @('WrongInsideTarget', 'OutsideTarget', 'NestedAlias', 'DirectoryAlias', 'BrokenAlias', 'UnrelatedAlias')) {
    Test-Case ('Reject-' + $variant) {
        $f = New-Fixture $variant
        $alias = Join-Path $f.Python 'python3.exe'; $target = $f.Executable; $directory = $false
        if ($variant -eq 'WrongInsideTarget') { $target = Join-Path $f.Python 'other.exe'; Copy-Item -LiteralPath $f.Executable -Destination $target }
        if ($variant -in @('OutsideTarget', 'BrokenAlias')) { $target = Join-Path $f.Root 'outside.exe'; Copy-Item -LiteralPath $f.Executable -Destination $target }
        if ($variant -eq 'NestedAlias') { $alias = Join-Path $f.Python 'Lib/python3.exe' }
        if ($variant -eq 'DirectoryAlias') { $target = Join-Path $f.Root 'outside-directory'; New-Item -ItemType Directory -Path $target | Out-Null; $directory = $true }
        if ($variant -eq 'UnrelatedAlias') { $alias = Join-Path $f.Python 'launcher.exe' }
        New-Link $alias $target $directory
        if ($variant -eq 'BrokenAlias') { Remove-Item -LiteralPath $target }
        $before = Get-ContractHash $f.Executable; $rejected = $false
        try { Invoke-Stage $f } catch {
            if ($_.Exception.Message -notlike '*reparse*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "An unsupported reparse entry was accepted: $variant" }
        if ((Get-ContractHash $f.Executable) -cne $before) { throw 'The borrowed selected interpreter was changed.' }
    }
}
Test-Case 'VerifyOnlyRejectsRetargetedOmittedAliasWithIdenticalBytes' {
    $f = New-Fixture 'retargeted'; $alias = Join-Path $f.Python 'python3.exe'; New-Link $alias $f.Executable
    Invoke-Stage $f
    Remove-Item -LiteralPath $alias
    $other = Join-Path $f.Root 'same-bytes.exe'; Copy-Item -LiteralPath $f.Executable -Destination $other; New-Link $alias $other
    $rejected = $false
    try { & $ProductionScript -OutputDirectory $f.Output -VerifyOnly } catch { $rejected = $true }
    if (-not $rejected) { throw 'VerifyOnly accepted a different target with identical bytes.' }
}
$summary = [ordered]@{ Passed=@($results | Where-Object Passed).Count; Failed=@($results | Where-Object { -not $_.Passed -and -not $_.Skipped }).Count;
    Skipped=@($results | Where-Object Skipped).Count; LinkCapability=$linkCapability; Results=$results; NoPythonOrBashExecution=$true;
    FixtureQualification='Real Windows file/directory symbolic links and ordinary file copies; fixture payloads are explicitly nonrunnable. A separate full installed-runtime staging check is required.' }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding utf8
Write-Output ("Interpreter staging contracts: {0} passed, {1} failed, {2} capability-qualified skips." -f $summary.Passed, $summary.Failed, $summary.Skipped)
if ($summary.Failed -ne 0) { exit 1 }
