param([Parameter(Mandatory)][string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$json = & rustup run 1.97.0 cargo metadata --locked --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --format-version 1
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect pinned native Monty dependency licenses.' }
$metadata = ($json -join "`n") | ConvertFrom-Json
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('# Native Monty dependency notices')
$notices.Add('')
$notices.Add('Generated from the committed Cargo lockfile. Original license files follow in the named package folders.')
foreach ($package in ($metadata.packages | Where-Object { $_.source } | Sort-Object name, version)) {
    $key = $package.name + '-' + $package.version
    $root = Split-Path -Parent $package.manifest_path
    $files = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)([.\-_].*)?$' })
    if ($package.license_file) {
        $declared = [IO.Path]::GetFullPath((Join-Path $root $package.license_file))
        if (-not (Test-Path -LiteralPath $declared -PathType Leaf)) { throw "Declared license file is missing for $key." }
        $files += Get-Item -LiteralPath $declared
    }
    $files = @($files | Sort-Object FullName -Unique)
    if ($files.Count -eq 0 -and $key -eq 'r-efi-5.3.0') {
        # This exact crate's README identifies AUTHORS as its complete license text.
        $files = @(Get-Item -LiteralPath (Join-Path $root 'AUTHORS'))
    }
    if ($files.Count -eq 0 -and $key -in @('monty-0.0.23', 'monty-types-0.0.23', 'monty-alloc-0.0.23', 'monty-macros-0.0.23')) {
        $vcs = Get-Content -LiteralPath (Join-Path $root '.cargo_vcs_info.json') -Raw | ConvertFrom-Json
        if ($vcs.git.sha1 -ne '302e0f27ec8516689ba90793cd0381149e5f4076') { throw "Unexpected source commit for $key." }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'license-overrides/monty-0.0.23') -File)
    }
    if ($files.Count -eq 0 -and $package.name -like 'ruff_*' -and $package.version -eq '0.0.9') {
        $vcs = Get-Content -LiteralPath (Join-Path $root '.cargo_vcs_info.json') -Raw | ConvertFrom-Json
        if ($vcs.git.sha1 -ne 'b0e47022cfce4f3594aa26d15ea792681430b6f6') { throw "Unexpected source commit for $key." }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'license-overrides/ruff-0.0.9') -File)
    }
    if ($files.Count -eq 0 -and $key -in @('quote-use-0.8.4', 'quote-use-macros-0.8.4')) {
        $vcs = Get-Content -LiteralPath (Join-Path $root '.cargo_vcs_info.json') -Raw | ConvertFrom-Json
        if ($vcs.git.sha1 -ne '05096f346f8b17fba8a57a235b958917ad9d99e0') { throw "Unexpected source commit for $key." }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'license-overrides/quote-use-0.8.4') -File)
    }
    if ($files.Count -eq 0) { throw "No original license file found for native dependency $key ($($package.license))." }
    $destination = Join-Path $OutputDirectory $key
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $files) {
        $copied = Join-Path $destination $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $copied
        # ZIP cannot represent original registry timestamps before 1980. Preserve
        # license bytes while giving only those copied files a representable date.
        if ([IO.File]::GetLastWriteTimeUtc($copied).Year -lt 1980) { [IO.File]::SetLastWriteTimeUtc($copied, [DateTime]::new(1980, 1, 2, 0, 0, 0, [DateTimeKind]::Utc)) }
    }
    $notices.Add('')
    $notices.Add(('## ' + $key))
    $notices.Add(('SPDX: ' + $package.license))
    $notices.Add(('Source: ' + $package.repository))
    $label = if ($package.name -in @('quote-use', 'quote-use-macros')) { 'Declared-license attribution files (see SOURCE.md): ' } else { 'Original license files: ' }
    $notices.Add(($label + (($files | ForEach-Object { $key + '/' + $_.Name }) -join ', ')))
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.md'), ([string]::Join("`n", $notices) + "`n"), [Text.UTF8Encoding]::new($false))
