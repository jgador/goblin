#!/usr/bin/env bash
# Install development tools as the current WSL/Linux user, never through sudo.
set -euo pipefail
goblin_git_repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$goblin_git_repository"

goblin_cargo_bin="${CARGO_HOME:-$HOME/.cargo}/bin"
export PATH="$goblin_cargo_bin:$PATH"
if ! command -v rustup >/dev/null 2>&1; then
    goblin_rustup=$(mktemp)
    trap 'rm -f "$goblin_rustup"' EXIT
    curl --proto '=https' --tlsv1.2 --fail --silent --show-error \
        https://sh.rustup.rs -o "$goblin_rustup"
    sh "$goblin_rustup" -y --profile minimal --default-toolchain none
fi

# rust-toolchain.toml supplies the compiler version and required components.
rustup show active-toolchain
rustup target add x86_64-unknown-linux-musl

# These versions match Codex's CI helper pins.
cargo install --locked just --version 1.51.0
cargo install --locked cargo-nextest --version 0.9.103
# Match Codex's snapshot library; its install guide leaves the CLI unpinned.
cargo install --locked cargo-insta --version 1.46.3
# Codex's install guide also installs DotSlash without a version pin.
cargo install --locked dotslash --version 0.5.7

printf '\nRust development tools installed. In an existing terminal, run:\n'
printf '  source "%s/env"\n' "${CARGO_HOME:-$HOME/.cargo}"
