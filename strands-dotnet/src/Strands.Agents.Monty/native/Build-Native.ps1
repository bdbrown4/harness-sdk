param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x86', 'win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [string] $OutputDirectory,
    [string] $BuildDirectory
)

$ErrorActionPreference = 'Stop'
$sdkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sdkRoot 'artifacts/monty-native' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $sdkRoot 'artifacts/monty-build' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$targets = @{
    'win-x86' = 'i686-pc-windows-msvc'; 'win-x64' = 'x86_64-pc-windows-msvc'
    'linux-x64' = 'x86_64-unknown-linux-gnu'
    'osx-x64' = 'x86_64-apple-darwin'; 'osx-arm64' = 'aarch64-apple-darwin'
}
$target = $targets[$RuntimeIdentifier]
$oldTargetDirectory = $env:CARGO_TARGET_DIR
$oldRustFlags = $env:RUSTFLAGS
try {
    $env:CARGO_TARGET_DIR = $BuildDirectory
    if ($RuntimeIdentifier.StartsWith('win-')) {
        # A clean Framework installation must not require an additional VC runtime.
        $env:RUSTFLAGS = ($oldRustFlags + ' -C target-feature=+crt-static').Trim()
    }
    $manifest = Join-Path $PSScriptRoot 'Cargo.toml'
    & rustup run 1.97.0 cargo fmt --manifest-path $manifest --check
    if ($LASTEXITCODE -ne 0) { throw 'Monty bridge formatting failed.' }
    & rustup run 1.97.0 cargo test --locked --manifest-path $manifest --target $target
    if ($LASTEXITCODE -ne 0) { throw "Native Monty tests failed for $RuntimeIdentifier." }
    & rustup run 1.97.0 cargo build --locked --release --manifest-path $manifest --target $target --bin strands_monty_worker
    if ($LASTEXITCODE -ne 0) { throw "Native Monty build failed for $RuntimeIdentifier." }
    $filename = if ($RuntimeIdentifier.StartsWith('win-')) { 'strands_monty_worker.exe' } else { 'strands_monty_worker' }
    $source = Join-Path $BuildDirectory ($target + '/release/' + $filename)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing built native worker: $source" }
    $destination = Join-Path $OutputDirectory ('runtimes/' + $RuntimeIdentifier + '/native')
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination $filename)
    $version = & (Join-Path $destination $filename) --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^strands_monty_worker ABI1 Monty0\.0\.23 (32|64)-bit$') { throw "Unexpected native worker identity: $version" }
    & (Join-Path $PSScriptRoot 'Export-Licenses.ps1') -OutputDirectory (Join-Path $OutputDirectory 'licenses')
}
finally {
    $env:CARGO_TARGET_DIR = $oldTargetDirectory
    $env:RUSTFLAGS = $oldRustFlags
}
