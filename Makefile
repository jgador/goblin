SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c
.DEFAULT_GOAL := help

# Asset copying and test-tool cleanup share outputs, so preserve workflow order.
.NOTPARALLEL:
export PATH := $(CURDIR)/node_modules/.bin:$(PATH)
CARGO_ARGS ?= --workspace
ARGS ?=
RUST_MIN_STACK ?= 8388608

.PHONY: help install install-node install-rust setup-rust build build-assets \
	build-tools build-backend build-native typecheck typecheck-scripts check dev start \
	test test-backend test-js test-frontend test-rust test-rust-doc test-release \
	test-contracts test-task-runner test-secrets test-codex test-headlamp test-browser browser-install \
	test-logs-ui test-logging format format-typescript format-rust format-rust-check \
	format-backend lint-rust fix-rust protocol-generate protocol-check \
	kubernetes-generate kubernetes-check contracts-generate contracts-check \
	deploy-generate dependencies-check dependencies-resolve secrets-setup secrets-scan \
	secrets-staged secrets-history goblinctl local local-start local-status local-stop \
	local-reset local-logs local-retry local-database local-password setup-password \
	release-build release-check-installer release-local codex

# List targets and their purpose; pass tool flags with ARGS or CARGO_ARGS.
help:
	@awk '/^# / { description = substr($$0, 3); next } /^[a-z][a-z-]*:/ { printf "  %-25s %s\n", $$1, description }' $(MAKEFILE_LIST)

# Install locked Node packages and fetch the pinned Rust workspace dependencies.
install: install-node install-rust

# Install repository tooling and frontend packages from their separate lockfiles.
install-node:
	npm ci
	npm --prefix frontend ci

# Load the pinned Rust toolchain and fetch locked workspace dependencies.
install-rust:
	rustup show active-toolchain
	cargo fetch --locked

# Install the pinned Rust compiler and development helpers as the current user.
setup-rust:
	bash scripts/setup-rust.sh

# Build native tools, frontend assets, test tooling, and the .NET solution in order.
build: build-native build-assets build-tools build-backend

# Delegate browser asset compilation to the frontend npm package.
build-assets:
	npm --prefix frontend run build

# Clean compiled test tooling, check direct Node scripts, and compile test tools.
build-tools:
	node scripts/clean-output.mts
	$(MAKE) typecheck-scripts
	tsc -p tsconfig.json

# Build the .NET solution using the existing frontend output.
build-backend:
	dotnet build backend/Goblin.slnx --nologo

# Build Rust tools; override CARGO_ARGS to select a crate or target.
build-native:
	cargo build --locked $(CARGO_ARGS)

# Check generated contracts, browser source, Node tooling, and the .NET build.
typecheck: contracts-check typecheck-scripts
	npm --prefix frontend run typecheck
	tsc -p tsconfig.json --noEmit
	$(MAKE) build-backend

# Check directly executed TypeScript scripts without emitting files.
typecheck-scripts:
	tsc -p tsconfig.scripts.json

# Run formatting checks, Rust lints, the full offline suite, and type checks.
check: format-rust-check lint-rust test typecheck

# Build the application, provision local credentials, and launch the .NET host.
dev: build
	cargo xtask dev $(ARGS)

# Start the development host with the same build and credential setup as dev.
start: dev

# Preserve all pretest checks, builds, generators, and offline application tests.
test: contracts-check test-contracts test-task-runner dependencies-check build deploy-generate test-rust test-release protocol-check kubernetes-check test-backend test-js

# Run .NET tests after building the backend; real database checks remain opt-in.
test-backend: build-backend
	dotnet test backend/Goblin.slnx --no-build --nologo $(ARGS)

# Run compiled frontend, HTTP integration, and deployment contract tests.
test-js: build-tools
	node --test dist/tests/frontend/*.test.js dist/tests/integration/*.test.js dist/tests/deployment/*.test.js

# Build Node test tooling and run the frontend contract tests.
test-frontend: build-tools
	node --test dist/tests/frontend/*.test.js

# Run nextest with the existing local profile and stack size, then doc tests.
test-rust:
	RUST_MIN_STACK=$(RUST_MIN_STACK) NEXTEST_PROFILE=local cargo nextest run --locked --no-fail-fast $(CARGO_ARGS) $(ARGS)
	$(MAKE) test-rust-doc

# Run Rust documentation tests, which nextest does not execute.
test-rust-doc:
	cargo test --locked --doc $(CARGO_ARGS)

# Run release tooling and publication contract tests directly with Node.
test-release:
	node --test scripts/release-*.test.mts

# Test contract generation, including rejection of stale or invalid definitions.
test-contracts:
	node --test scripts/generate-contract-values.test.mts

# Verify Make workflow ordering, failure propagation, and explicit flag forwarding.
test-task-runner:
	node --test scripts/make-workflows.test.mts

# Test secret scanning and Git hooks with the installed Gitleaks CLI.
test-secrets:
	node --test scripts/check-secrets.test.mts

# Build and check the pinned Codex runtime using isolated test credentials.
test-codex: build
	node dist/scripts/check-codex.js

# Build and check a configured real Headlamp service.
test-headlamp: build
	node dist/scripts/check-headlamp.js

# Build and run Playwright journeys; ARGS selects tests or browser options.
test-browser: build
	playwright test $(ARGS)

# Install Playwright's pinned Chromium revision and its operating-system libraries.
browser-install:
	playwright install --with-deps chromium

# Build and check a configured real VictoriaLogs UI.
test-logs-ui: build
	node dist/scripts/check-logs-ui.js

# Build and run the existing disposable-cluster logging smoke test.
test-logging: build
	bash scripts/check-logging.sh

# Format TypeScript, Rust, and handwritten C# using the existing conventions.
format: format-typescript format-rust format-backend

# Format repository TypeScript while respecting ignored files and embedded text.
format-typescript:
	prettier --write --ignore-path .gitignore "**/*.{ts,tsx,mts,cts}"

# Format Rust with one imported item per use statement.
format-rust:
	cargo fmt --all -- --config imports_granularity=Item

# Check Rust formatting without writing files.
format-rust-check:
	cargo fmt --all -- --config imports_granularity=Item --check

# Format handwritten C# while excluding generated protocol and EF files.
format-backend:
	cd backend && dotnet format whitespace Goblin.slnx --exclude src/Goblin.Protocol/Generated src/Goblin.Persistence/Generated
	cd backend && dotnet format style Goblin.slnx --severity info --exclude-diagnostics IDE0130 IDE1006 --exclude src/Goblin.Protocol/Generated src/Goblin.Persistence/Generated

# Lint selected Rust crates and all targets with warnings denied.
lint-rust:
	cargo clippy --locked --tests $(CARGO_ARGS) --all-targets -- -D warnings $(ARGS)

# Apply Clippy fixes to selected Rust crates and tests; review the resulting diff.
fix-rust:
	cargo clippy --locked --fix --tests --allow-dirty $(CARGO_ARGS) $(ARGS)

# Regenerate Codex protocol models from the checked-in schemas and selection.
protocol-generate:
	dotnet run --file backend/scripts/GenerateProtocol.cs

# Run protocol generator self-tests and reject stale generated models.
protocol-check:
	dotnet run --file backend/scripts/GenerateProtocol.cs -- --self-test --check

# Regenerate selected Kubernetes and Agent Sandbox models from pinned schemas.
kubernetes-generate:
	dotnet run --file backend/scripts/GenerateKubernetes.cs

# Run Kubernetes generator self-tests and reject stale generated models.
kubernetes-check:
	dotnet run --file backend/scripts/GenerateKubernetes.cs -- --self-test --check

# Regenerate browser contract values from owning C# and Rust definitions.
contracts-generate:
	node scripts/generate-contract-values.mts

# Reject stale generated browser contract values without writing files.
contracts-check:
	node scripts/generate-contract-values.mts --check

# Generate Azure review assets offline using the pinned standalone Bicep CLI.
deploy-generate:
	cargo xtask azure

# Verify the locked deployment dependency catalog and generated references.
dependencies-check:
	cargo xtask dependencies check --locked

# Resolve deployment dependencies using the existing explicit update workflow.
dependencies-resolve:
	cargo xtask dependencies resolve $(ARGS)

# Install secret-scanning hooks without replacing an existing hook configuration.
secrets-setup:
	node scripts/install-git-hooks.mts

# Scan the index and working tree with the existing Gitleaks safety rules.
secrets-scan:
	node scripts/check-secrets.mts

# Scan staged file contents before committing.
secrets-staged:
	node scripts/check-secrets.mts staged

# Scan all reachable commit history before pushing.
secrets-history:
	node scripts/check-secrets.mts history

# Run the operator CLI from source with arguments supplied through ARGS.
goblinctl:
	cargo run --locked --package goblinctl -- $(ARGS)

# Run a local installer command from source with arguments supplied through ARGS.
local:
	cargo run --locked -q -p goblinctl -- local $(ARGS)

# Start or update the full local installation, preserving installer safety checks.
local-start:
	cargo run --locked -q -p goblinctl -- local start $(ARGS)

# Report local installation status without starting or resetting it.
local-status:
	cargo run --locked -q -p goblinctl -- local status $(ARGS)

# Stop the owned local installation using the existing operator CLI.
local-stop:
	cargo run --locked -q -p goblinctl -- local stop $(ARGS)

# Reset the owned local installation; explicit ARGS=--yes remains required.
local-reset:
	cargo run --locked -q -p goblinctl -- local reset $(ARGS)

# Read local installation logs; use ARGS=--follow to stream them.
local-logs:
	cargo run --locked -q -p goblinctl -- local logs $(ARGS)

# Explicitly retry a failed local installation using the operator CLI.
local-retry:
	cargo run --locked -q -p goblinctl -- local retry $(ARGS)

# Provision the local PostgreSQL development database.
local-database:
	cargo run --locked -q -p goblinctl -- local database $(ARGS)

# Report the local password verifier location through the operator CLI.
local-password:
	cargo run --locked -q -p goblinctl -- local password $(ARGS)

# Choose and confirm a local password; use ARGS=--replace to replace its verifier.
setup-password:
	cargo run --locked -q -p goblinctl -- password set $(ARGS)

# Build the release CLI through the shared fingerprinted build recipe.
release-build:
	bash scripts/build-goblinctl-release.sh target

# Check installer contracts and release compiler-input coverage after a build.
release-check-installer:
	cargo xtask release check-installer

# Build and package the Linux musl operator CLI for local review.
release-local: release-build
	cargo xtask package --binary target/x86_64-unknown-linux-musl/release/goblinctl

# Invoke the locked Codex npm runtime directly without downloading another version.
codex:
	node node_modules/@openai/codex/bin/codex.js $(ARGS)
