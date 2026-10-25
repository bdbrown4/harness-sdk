#!/usr/bin/env bash
set -euo pipefail

native_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
output_dir="${1:?Pass the native asset output directory}"
build_dir="${2:?Pass the isolated build directory}"
target=x86_64-unknown-linux-gnu
mkdir -p "$output_dir/runtimes/linux-x64/native" "$build_dir"
ldd --version
rustup run 1.97.0 rustc --version
export CARGO_TARGET_DIR="$build_dir"
manifest="$native_dir/Cargo.toml"
rustup run 1.97.0 cargo fmt --manifest-path "$manifest" --check
rustup run 1.97.0 cargo test --locked --manifest-path "$manifest" --target "$target"
rustup run 1.97.0 cargo build --locked --release --manifest-path "$manifest" --target "$target" --bin strands_monty_worker
worker="$CARGO_TARGET_DIR/$target/release/strands_monty_worker"
test -s "$worker"
test -x "$worker"
"$worker" --version
cp "$worker" "$output_dir/runtimes/linux-x64/native/"
readelf --version-info "$worker"
