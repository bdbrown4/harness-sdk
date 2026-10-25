param(
    [Parameter(Mandatory = $true)][string] $RepositoryRoot,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [string] $WorkflowPath,
    [string] $ExtractorScript
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not $WorkflowPath) { $WorkflowPath = Join-Path $root '.github/workflows/ci.yml' }
if (-not $ExtractorScript) { $ExtractorScript = Join-Path $root 'strands-dotnet/src/Strands.Agents.Monty/native/Expand-NativeArtifacts.ps1' }
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new contract evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Test-Case([string] $Name, [scriptblock] $Body) {
    try { & $Body; $results.Add([pscustomobject]@{ Name=$Name; Passed=$true }) }
    catch { $results.Add([pscustomobject]@{ Name=$Name; Passed=$false; Error=$_.Exception.Message }) }
}
function Get-ContractHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}

# Package input inventory is independent of the workflow's explicit path list.
$patterns = [Collections.Generic.List[string]]::new()
$selected = $false
foreach ($line in Get-Content -LiteralPath $WorkflowPath) {
    if ($line -match '^            ([\w-]+):\s*$') { $selected = $Matches[1] -in @('dotnet', 'dotnet-cedar', 'dotnet-monty') }
    if ($selected -and $line -match "^              - '([^']+)'\s*$") { $patterns.Add($Matches[1]) }
}
$projects = @(Get-ChildItem -LiteralPath (Join-Path $root 'strands-dotnet/src') -Filter '*.csproj' -Recurse -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'harness-dotnet/src') -Filter '*.csproj' -Recurse -File)
foreach ($project in $projects) {
    [xml] $xml = Get-Content -Raw -LiteralPath $project.FullName
    if (@($xml.Project.PropertyGroup.IsPackable) -contains 'false') { continue }
    foreach ($item in $xml.SelectNodes('/Project/ItemGroup/None[@Pack="true"]')) {
        if ([IO.Path]::GetFileName([string] $item.Include) -cne 'README.md') { continue }
        $input = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName ([string] $item.Include)))
        $relative = $input.Substring($root.Length).TrimStart([IO.Path]::DirectorySeparatorChar).Replace('\', '/')
        Test-Case ('PackagedReadmeRoutesToValidation-' + $project.BaseName) {
            $covered = @($patterns | Where-Object {
                $_ -ceq $relative -or ($_.EndsWith('/**', [StringComparison]::Ordinal) -and $relative.StartsWith($_.Substring(0, $_.Length - 2), [StringComparison]::Ordinal))
            })
            if ($covered.Count -eq 0) { throw "Packaged README change does not schedule .NET validation: $relative" }
        }
    }
}

$rids = @('win-x86', 'win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
$archives = Join-Path $EvidenceDirectory 'archives'
New-Item -ItemType Directory -Path $archives | Out-Null
$unix = [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT
foreach ($rid in $rids) {
    $fixture = Join-Path $EvidenceDirectory ('fixture-' + $rid)
    $native = Join-Path $fixture ("runtimes/$rid/native")
    $licenses = Join-Path $fixture 'licenses'
    New-Item -ItemType Directory -Path $native, $licenses | Out-Null
    $filename = if ($rid.StartsWith('win-')) { 'strands_monty_worker.exe' } else { 'strands_monty_worker' }
    $worker = Join-Path $native $filename
    [IO.File]::WriteAllText($worker, "#!/bin/sh`nprintf 'artifact-permission-fixture'`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $licenses 'THIRD-PARTY-NOTICES.md'), 'license-byte-fixture', [Text.UTF8Encoding]::new($false))
    if ($unix -and -not $rid.StartsWith('win-')) {
        & python -c 'import os,sys; os.chmod(sys.argv[1],0o755)' $worker
        if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare executable permission fixture.' }
    }
    & tar -cf (Join-Path $archives ("monty-native-$rid.tar")) -C $fixture "runtimes/$rid" licenses
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare real fixture tar.' }
}
Test-Case 'FiveRidTarHandoffPreservesPayloadAndUnixLaunch' {
    $destination = Join-Path $EvidenceDirectory 'roundtrip'
    & $ExtractorScript -ArchiveDirectory $archives -DestinationDirectory $destination
    foreach ($rid in $rids) {
        $filename = if ($rid.StartsWith('win-')) { 'strands_monty_worker.exe' } else { 'strands_monty_worker' }
        $relative = "runtimes/$rid/native/$filename"
        $actual = Join-Path $destination $relative
        $expected = Join-Path (Join-Path $EvidenceDirectory ('fixture-' + $rid)) $relative
        if (-not (Test-Path -LiteralPath $actual -PathType Leaf) -or
            (Get-ContractHash $actual) -cne (Get-ContractHash $expected)) { throw "Extracted worker bytes are missing or changed: $rid" }
        if ($unix -and -not $rid.StartsWith('win-')) {
            & python -c 'import os,stat,subprocess,sys; p=sys.argv[1]; assert stat.S_IMODE(os.stat(p).st_mode)&0o111; r=subprocess.run([p],check=True,stdout=subprocess.PIPE,text=True); assert r.stdout=="artifact-permission-fixture"' $actual
            if ($LASTEXITCODE -ne 0) { throw "Extracted worker is not executable without permission repair: $rid" }
        }
    }
    if ([IO.File]::ReadAllText((Join-Path $destination 'licenses/THIRD-PARTY-NOTICES.md')) -cne 'license-byte-fixture') { throw 'License payload changed.' }
}
foreach ($clock in @(
    @{ Name='EpochNativeFileDatesAreZipCompatible'; Source=[DateTime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc); Expected=[DateTime]::new(1980, 1, 2, 0, 0, 0, [DateTimeKind]::Utc) },
    @{ Name='RepresentableNativeFileDatesAreUnchanged'; Source=[DateTime]::new(2011, 2, 3, 12, 34, 56, [DateTimeKind]::Utc); Expected=[DateTime]::new(2011, 2, 3, 12, 34, 56, [DateTimeKind]::Utc) }
)) {
    Test-Case $clock.Name {
        $inputs = Join-Path $EvidenceDirectory ('archives-' + $clock.Name)
        New-Item -ItemType Directory -Path $inputs | Out-Null
        $archiveHashes = @{}
        foreach ($rid in $rids) {
            $fixture = Join-Path $EvidenceDirectory ('fixture-' + $rid)
            foreach ($file in Get-ChildItem -LiteralPath $fixture -Recurse -File) { [IO.File]::SetLastWriteTimeUtc($file.FullName, $clock.Source) }
            $archive = Join-Path $inputs ("monty-native-$rid.tar")
            & tar -cf $archive -C $fixture "runtimes/$rid" licenses
            if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare timestamp fixture tar.' }
            $archiveHashes[$rid] = Get-ContractHash $archive
        }
        $destination = Join-Path $EvidenceDirectory ('roundtrip-' + $clock.Name)
        & $ExtractorScript -ArchiveDirectory $inputs -DestinationDirectory $destination
        foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
            if ($file.LastWriteTimeUtc -ne $clock.Expected) { throw "Unexpected extracted native file timestamp: $($file.FullName) = $($file.LastWriteTimeUtc.ToString('o'))" }
        }
        foreach ($rid in $rids) {
            $filename = if ($rid.StartsWith('win-')) { 'strands_monty_worker.exe' } else { 'strands_monty_worker' }
            $relative = "runtimes/$rid/native/$filename"
            $actual = Join-Path $destination $relative
            $expected = Join-Path (Join-Path $EvidenceDirectory ('fixture-' + $rid)) $relative
            if ((Get-ContractHash $actual) -cne (Get-ContractHash $expected) -or
                (Get-ContractHash (Join-Path $inputs ("monty-native-$rid.tar"))) -cne $archiveHashes[$rid]) { throw "Timestamp normalization changed native bytes or its source tar: $rid" }
            if ($unix -and -not $rid.StartsWith('win-')) {
                & python -c 'import os,stat,subprocess,sys; p=sys.argv[1]; assert stat.S_IMODE(os.stat(p).st_mode)==0o755; r=subprocess.run([p],check=True,stdout=subprocess.PIPE,text=True); assert r.stdout=="artifact-permission-fixture"' $actual
                if ($LASTEXITCODE -ne 0) { throw "Timestamp normalization changed executable permission or launch: $rid" }
            }
        }
        if ([IO.File]::ReadAllText((Join-Path $destination 'licenses/THIRD-PARTY-NOTICES.md')) -cne 'license-byte-fixture') { throw 'Timestamp normalization changed notice bytes.' }
    }
}
foreach ($variant in @('MissingRid', 'OutsideRuntime', 'MissingNotices', 'MalformedTar')) {
    Test-Case ('RejectNativeArchive-' + $variant) {
        $inputs = Join-Path $EvidenceDirectory ('inputs-' + $variant)
        New-Item -ItemType Directory -Path $inputs | Out-Null
        Copy-Item -Path (Join-Path $archives '*.tar') -Destination $inputs
        $changed = Join-Path $inputs 'monty-native-linux-x64.tar'
        if ($variant -eq 'MissingRid') { Remove-Item -LiteralPath $changed }
        elseif ($variant -eq 'MalformedTar') { [IO.File]::WriteAllText($changed, 'not-a-tar') }
        else {
            $fixture = Join-Path $EvidenceDirectory 'fixture-linux-x64'
            if ($variant -eq 'OutsideRuntime') {
                [IO.File]::WriteAllText((Join-Path $fixture 'outside.txt'), 'outside')
                & tar -cf $changed -C $fixture 'runtimes/linux-x64' licenses outside.txt
            } else { & tar -cf $changed -C $fixture 'runtimes/linux-x64' }
            if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare boundary fixture tar.' }
        }
        $destination = Join-Path $EvidenceDirectory ('rejected-' + $variant)
        $rejected = $false
        try { & $ExtractorScript -ArchiveDirectory $inputs -DestinationDirectory $destination }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid native artifact set was accepted: $variant" }
        if (Test-Path -LiteralPath $destination) {
            if (@(Get-ChildItem -LiteralPath $destination -Recurse -File).Count -ne 0) { throw 'Artifact validation must reject before extracting any payload.' }
        }
    }
}
$report = [pscustomobject]@{ Scope='CI script/archive fixtures only; no native runtime or hosted CI claim'; UnixExecutableLaunchExercised=$unix; PowerShellVersion=$PSVersionTable.PSVersion.ToString(); Passed=@($results | Where-Object Passed).Count; Failed=@($results | Where-Object { -not $_.Passed }).Count; Results=$results }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 6
if ($report.Failed -ne 0) { exit 1 }
exit 0
