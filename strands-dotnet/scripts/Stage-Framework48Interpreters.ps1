[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [string] $PythonExecutable,
    [string] $GitDirectory,
    [switch] $VerifyOnly
)
$ErrorActionPreference = 'Stop'
function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Get-OmittedPythonAlias([IO.FileSystemInfo] $Entry, [string] $SelectedExecutable) {
    if ($Entry -isnot [IO.FileInfo] -or $Entry.Name -ine 'python3.exe' -or $Entry.LinkType -cne 'SymbolicLink') { return $null }
    $selected = Get-Item -LiteralPath $SelectedExecutable -Force
    if ($selected -isnot [IO.FileInfo] -or ($selected.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $null }
    if (-not [string]::Equals($Entry.DirectoryName, $selected.DirectoryName, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $targets = @($Entry.Target)
    if ($targets.Count -ne 1 -or [string]::IsNullOrEmpty([string] $targets[0])) { return $null }
    $target = [string] $targets[0]
    if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $Entry.DirectoryName $target }
    $target = [IO.Path]::GetFullPath($target)
    # Only the unused direct alias of the selected physical file is omitted; no link chain is followed.
    if (-not [string]::Equals($target, $selected.FullName, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $hash = Get-Sha256 $selected.FullName
    if ((Get-Sha256 $Entry.FullName) -cne $hash) { throw 'The Python alias identity changed during staging.' }
    return [pscustomobject]@{ Source=$Entry.FullName; Target=$selected.FullName; SHA256=$hash }
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $output 'interpreter-manifest.json'
if ($VerifyOnly) {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    foreach ($file in $manifest.Files) {
        if ((Get-Sha256 $file.Source) -cne $file.SHA256 -or (Get-Sha256 (Join-Path $output $file.RelativePath)) -cne $file.SHA256) {
            throw "A staged interpreter or its original source changed: $($file.RelativePath)"
        }
    }
    foreach ($alias in $manifest.OmittedPythonAliases) {
        $current = Get-OmittedPythonAlias (Get-Item -LiteralPath $alias.Source -Force) $manifest.PythonSourceExecutable
        if ($null -eq $current -or -not [string]::Equals($current.Target, $alias.Target, [StringComparison]::OrdinalIgnoreCase) -or $current.SHA256 -cne $alias.SHA256) {
            throw "An omitted Python alias changed: $($alias.Source)"
        }
    }
    Write-Output "Verified $($manifest.Files.Count) unchanged interpreter inputs/copies."
    return
}
if (Test-Path -LiteralPath $output) { throw 'Use a fresh, dedicated interpreter staging directory.' }
if (-not $PythonExecutable) { $PythonExecutable = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$PythonExecutable = (Resolve-Path -LiteralPath $PythonExecutable).Path
if (-not [IO.Path]::IsPathRooted($PythonExecutable) -or -not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) { throw 'An existing setup-python-selected interpreter is required.' }
if (-not $GitDirectory) { $GitDirectory = Join-Path $env:ProgramFiles 'Git' }
$GitDirectory = (Resolve-Path -LiteralPath $GitDirectory).Path
$bashBin = Join-Path $GitDirectory 'usr/bin'
if (-not (Test-Path -LiteralPath (Join-Path $bashBin 'bash.exe') -PathType Leaf)) { throw 'The existing Git for Windows MSYS Bash runtime is required; WSL is not used.' }
$pythonRoot = Split-Path -Parent $PythonExecutable
foreach ($originalRoot in @($pythonRoot, $GitDirectory)) {
    $prefix = $originalRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ([string]::Equals($output, $originalRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Interpreter staging must stay outside its original runtime directories.'
    }
}
$files = [Collections.Generic.List[object]]::new()
$omittedAliases = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $output | Out-Null
function Copy-InterpreterFile([IO.FileInfo] $File, [string] $RelativePath) {
    if (($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Interpreter source must not contain reparse files: $($File.FullName)" }
    $hash = Get-Sha256 $File.FullName
    $destination = Join-Path $output $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $File.FullName -Destination $destination
    if ((Get-Sha256 $destination) -cne $hash -or (Get-Sha256 $File.FullName) -cne $hash) { throw 'Interpreter copy identity changed during staging.' }
    return [pscustomobject]@{ Source=$File.FullName; RelativePath=$RelativePath; Bytes=$File.Length; SHA256=$hash }
}
# Copy the already-installed runtime and its standard library; this is a test prerequisite, not a NuGet dependency.
foreach ($entry in Get-ChildItem -LiteralPath $pythonRoot -Recurse -Force) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $alias = Get-OmittedPythonAlias $entry $PythonExecutable
        if ($null -eq $alias) { throw "Interpreter source must not contain reparse entries: $($entry.FullName)" }
        $omittedAliases.Add($alias)
        continue
    }
    if ($entry -is [IO.FileInfo]) {
        $relative = $entry.FullName.Substring($pythonRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
        $files.Add((Copy-InterpreterFile $entry (Join-Path 'python' $relative)))
    }
}
foreach ($entry in Get-ChildItem -LiteralPath $bashBin -File) {
    $files.Add((Copy-InterpreterFile $entry (Join-Path 'bash/usr/bin' $entry.Name)))
}
foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $GitDirectory 'usr/share/licenses') -Recurse -File) {
    $relative = $entry.FullName.Substring($GitDirectory.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
    $files.Add((Copy-InterpreterFile $entry (Join-Path 'bash' $relative)))
}
$files.Add((Copy-InterpreterFile (Get-Item -LiteralPath (Join-Path $GitDirectory 'LICENSE.txt')) 'bash/LICENSE.txt'))
New-Item -ItemType Directory -Path (Join-Path $output 'bash/tmp') | Out-Null
[ordered]@{
    Scope='Declared external interpreter prerequisites for Framework tests only; no Core runtime or NuGet dependency; no interpreter execution, install or download'
    PythonExecutable=(Join-Path $output ('python/' + [IO.Path]::GetFileName($PythonExecutable)))
    PythonSourceExecutable=$PythonExecutable
    OmittedPythonAliases=$omittedAliases
    BashExecutable=(Join-Path $output 'bash/usr/bin/bash.exe')
    Files=$files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "Staged $($files.Count) hashed existing interpreter files; omitted $($omittedAliases.Count) unused selected-interpreter aliases; original sources unchanged."
