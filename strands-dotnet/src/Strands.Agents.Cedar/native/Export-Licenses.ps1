param([Parameter(Mandatory)][string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packages = @{}
$overrides = @{
    'cedar-policy-4.12.0' = 'cedar-4.12.0'
    'cedar-policy-core-4.12.0' = 'cedar-4.12.0'
    'cedar-policy-core-4.11.0' = 'cedar-4.11.0'
    'cedar-policy-formatter-4.12.0' = 'cedar-4.12.0'
    'cedar-policy-mcp-schema-generator-0.6.1' = 'schema-0.6.1'
    'mcp-tools-sdk-0.4.0' = 'mcp-tools-0.4.0'
    'r-efi-6.0.0' = 'r-efi-6.0.0'
}
foreach ($crate in @('engine', 'schema')) {
    $manifest = Join-Path $PSScriptRoot ($crate + '/Cargo.toml')
    $json = & rustup run 1.97.0 cargo metadata --locked --manifest-path $manifest --format-version 1
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect native dependency licenses for $crate." }
    $metadata = ($json -join "`n") | ConvertFrom-Json -AsHashtable
    foreach ($package in $metadata.packages) {
        if ($package.source) { $packages[($package.name + '-' + $package.version)] = $package }
    }
}
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('# Native Cedar dependency notices')
$notices.Add('')
$notices.Add('Generated from the two committed Cargo lockfiles. Original license files follow in the named package folders.')
foreach ($key in ($packages.Keys | Sort-Object)) {
    $package = $packages[$key]
    $root = Split-Path -Parent $package.manifest_path
    $files = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)([.\-_].*)?$' })
    if ($package.license_file) {
        $declared = [IO.Path]::GetFullPath((Join-Path $root $package.license_file))
        if (-not (Test-Path -LiteralPath $declared -PathType Leaf)) { throw "Declared license file is missing for $key." }
        $files += Get-Item -LiteralPath $declared
    }
    $files = @($files | Sort-Object FullName -Unique)
    if ($files.Count -eq 0 -and $overrides.ContainsKey($key)) {
        # These exact published archives omit their repository-level license
        # files. Checked-in originals identify the crate's exact VCS commit.
        $files = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot ('license-overrides/' + $overrides[$key])) -File)
    }
    if ($files.Count -eq 0) { throw "No original license file found for native dependency $key ($($package.license))." }
    $destination = Join-Path $OutputDirectory $key
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $files) {
        $copy = Join-Path $destination $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $copy
        # Several published Cargo archives use Unix-epoch timestamps. Keep the
        # original bytes while making the copied files representable in ZIP.
        $copiedFile = Get-Item -LiteralPath $copy
        if ($copiedFile.LastWriteTimeUtc.Year -lt 1980) {
            $copiedFile.LastWriteTimeUtc = [DateTime]::new(1980, 1, 2, 0, 0, 0, [DateTimeKind]::Utc)
        }
    }
    $notices.Add('')
    $notices.Add(('## ' + $key))
    $notices.Add(('SPDX: ' + $package.license))
    $notices.Add(('Source: ' + $package.repository))
    $notices.Add(('Original license files: ' + (($files | ForEach-Object { $key + '/' + $_.Name }) -join ', ')))
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.md'), ([string]::Join("`n", $notices) + "`n"), [Text.UTF8Encoding]::new($false))
