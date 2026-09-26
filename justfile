set positional-arguments
set shell := ["bash", "-euc"]

rust_min_stack := "8388608"

# List development commands.
help:
    just --list

# Load the pinned Rust toolchain and fetch locked workspace dependencies.
install:
    rustup show active-toolchain
    cargo fetch --locked

# Build Rust tools; accepts Cargo selectors such as -p goblinctl.
build *args:
    cargo build --locked "$@"

# Run the operator CLI from source.
goblinctl *args:
    cargo run --locked --package goblinctl -- "$@"

# Format Rust and this justfile using the Codex conventions.
fmt:
    just --unstable --fmt
    cargo fmt --all -- --config imports_granularity=Item

# Check Rust and justfile formatting without writing files.
fmt-check:
    just --unstable --fmt --check
    cargo fmt --all -- --config imports_granularity=Item --check

# Apply Clippy's suggested fixes to the selected crates.
fix *args:
    cargo clippy --locked --fix --tests --allow-dirty "$@"

# Lint the selected crates, including their tests.
clippy *args:
    cargo clippy --locked --tests "$@"

# Run Rust tests through nextest with Codex's local profile and stack size.
test *args:
    RUST_MIN_STACK={{ rust_min_stack }} NEXTEST_PROFILE=local cargo nextest run --locked --no-fail-fast "$@"

# nextest does not execute documentation tests.
test-doc *args:
    cargo test --locked --doc "$@"

# Build and package the Linux release binary for local review.
release:
    cargo build --locked --release --package goblinctl --target x86_64-unknown-linux-musl
    cargo xtask package --binary target/x86_64-unknown-linux-musl/release/goblinctl
