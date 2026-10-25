param(
    [Parameter(Mandatory = $true)][string] $ArchiveDirectory,
    [Parameter(Mandatory = $true)][string] $DestinationDirectory
)
$ErrorActionPreference = 'Stop'
$archives = (Resolve-Path -LiteralPath $ArchiveDirectory).Path
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$rids = @('win-x86', 'win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
$expectedNames = @($rids | ForEach-Object { "monty-native-$_.tar" })
$files = @(Get-ChildItem -LiteralPath $archives -File -Force)
if ($files.Count -ne $expectedNames.Count -or @($files | Where-Object { $_.Name -cnotin $expectedNames }).Count -ne 0) {
    throw 'Expected exactly the five native Monty RID tar artifacts.'
}
if (Test-Path -LiteralPath $destination) {
    $directory = Get-Item -LiteralPath $destination -Force
    if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) { throw 'Native Monty extraction requires a new or empty ordinary directory.' }
}
# Validate the narrow producer format completely before extracting any artifact.
foreach ($rid in $rids) {
    $archive = Join-Path $archives "monty-native-$rid.tar"
    $members = @(& tar -tf $archive)
    if ($LASTEXITCODE -ne 0) { throw "Cannot list native Monty artifact: $rid" }
    $types = @(& tar -tvf $archive)
    if ($LASTEXITCODE -ne 0 -or @($types | Where-Object { $_.Length -eq 0 -or $_[0] -notin @('-', 'd') }).Count -ne 0) {
        throw "Native Monty artifact may contain only ordinary files/directories: $rid"
    }
    $normalized = @($members | ForEach-Object {
        $name = ([string] $_).TrimEnd('/')
        if ($name.StartsWith('./', [StringComparison]::Ordinal)) { $name = $name.Substring(2) }
        if ($name.Contains('\') -or $name.StartsWith('/', [StringComparison]::Ordinal) -or $name -match '^[A-Za-z]:' -or
            @($name.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) { throw "Unsafe native Monty artifact path: $name" }
        $runtime = "runtimes/$rid"
        if ($name -cne 'runtimes' -and $name -cne $runtime -and -not $name.StartsWith($runtime + '/', [StringComparison]::Ordinal) -and
            $name -cne 'licenses' -and -not $name.StartsWith('licenses/', [StringComparison]::Ordinal)) { throw "Unexpected native Monty artifact path: $name" }
        $name
    })
    $filename = if ($rid.StartsWith('win-')) { 'strands_monty_worker.exe' } else { 'strands_monty_worker' }
    if ($normalized -cnotcontains "runtimes/$rid/native/$filename" -or $normalized -cnotcontains 'licenses/THIRD-PARTY-NOTICES.md') {
        throw "Native Monty artifact is missing its worker or original notices: $rid"
    }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($rid in $rids) {
    & tar -xf (Join-Path $archives "monty-native-$rid.tar") -C $destination
    if ($LASTEXITCODE -ne 0) { throw "Native Monty artifact extraction failed: $rid" }
}
# ZIP timestamps are local dates; the second UTC day also stays in range west of UTC.
foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
    if ($file.LastWriteTimeUtc.Year -lt 1980) {
        [IO.File]::SetLastWriteTimeUtc($file.FullName, [DateTime]::new(1980, 1, 2, 0, 0, 0, [DateTimeKind]::Utc))
    }
}
Write-Output 'Expanded all five native Monty RID artifacts with tar file modes preserved.'
