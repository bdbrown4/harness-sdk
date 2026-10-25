#!/usr/bin/env bash
set -euo pipefail

native_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
output_dir="${1:?Pass the native asset output directory}"
build_dir="${2:?Pass the isolated build directory}"
target=x86_64-unknown-linux-gnu
mkdir -p "$output_dir/runtimes/linux-x64/native" "$build_dir"
ldd --version
rustup run 1.97.0 rustc --version

for crate in abi engine schema; do
  export CARGO_TARGET_DIR="$build_dir/$crate"
  manifest="$native_dir/$crate/Cargo.toml"
  rustup run 1.97.0 cargo fmt --manifest-path "$manifest" --check
  rustup run 1.97.0 cargo test --locked --manifest-path "$manifest" --target "$target"
  if [[ "$crate" == abi ]]; then continue; fi
  rustup run 1.97.0 cargo build --locked --release --manifest-path "$manifest" --target "$target"
  library="$CARGO_TARGET_DIR/$target/release/libstrands_cedar_$crate.so"
  test -s "$library"
  cp "$library" "$output_dir/runtimes/linux-x64/native/"
  # Preserve exact ELF symbol-version requirements in CI diagnostics.
  readelf --version-info "$library"
done
