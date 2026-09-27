#!/usr/bin/env bash
set -euo pipefail
# One fingerprinted build recipe, shared by local packaging and release CI.
# The caller supplies a fresh target directory when publishing.
: "${1:?usage: build-goblinctl-release.sh TARGET_DIRECTORY}"
cargo build --locked --release --package goblinctl \
  --target x86_64-unknown-linux-musl --target-dir "$1"
