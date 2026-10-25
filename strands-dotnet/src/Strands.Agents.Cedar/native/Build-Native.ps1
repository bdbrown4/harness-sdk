param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x86', 'win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [string] $OutputDirectory,
    [string] $BuildDirectory
)

$ErrorActionPreference = 'Stop'
$sdkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sdkRoot 'artifacts/cedar-native' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $sdkRoot 'artifacts/cedar-build' }
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
    if ($RuntimeIdentifier.StartsWith('win-')) {
        # Framework consumers must not require a separate VC runtime install.
        $env:RUSTFLAGS = ($oldRustFlags + ' -C target-feature=+crt-static').Trim()
    }
    foreach ($crate in @('abi', 'engine', 'schema')) {
        $manifest = Join-Path $PSScriptRoot ($crate + '/Cargo.toml')
        $env:CARGO_TARGET_DIR = Join-Path $BuildDirectory $crate
        & rustup run 1.97.0 cargo fmt --manifest-path $manifest --check
        if ($LASTEXITCODE -ne 0) { throw "Rust formatting failed for $crate." }
        & rustup run 1.97.0 cargo test --locked --manifest-path $manifest --target $target
        if ($LASTEXITCODE -ne 0) { throw "Native Cedar tests failed for $crate/$target." }
        if ($crate -eq 'abi') { continue }
        & rustup run 1.97.0 cargo build --locked --release --manifest-path $manifest --target $target
        if ($LASTEXITCODE -ne 0) { throw "Native Cedar build failed for $crate/$target." }
        $filename = if ($RuntimeIdentifier.StartsWith('win-')) { "strands_cedar_$crate.dll" }
            elseif ($RuntimeIdentifier.StartsWith('osx-')) { "libstrands_cedar_$crate.dylib" }
            else { "libstrands_cedar_$crate.so" }
        $source = Join-Path $env:CARGO_TARGET_DIR ($target + '/release/' + $filename)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing built native library: $source" }
        $destination = Join-Path $OutputDirectory ('runtimes/' + $RuntimeIdentifier + '/native')
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $filename)
    }
    & (Join-Path $PSScriptRoot 'Export-Licenses.ps1') -OutputDirectory (Join-Path $OutputDirectory 'licenses')
}
finally {
    $env:CARGO_TARGET_DIR = $oldTargetDirectory
    $env:RUSTFLAGS = $oldRustFlags
}
