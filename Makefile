SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c
.DEFAULT_GOAL := help

# Asset copying and test-tool cleanup share outputs, so preserve workflow order.
.NOTPARALLEL:
export PATH := $(CURDIR)/node_modules/.bin:$(PATH)
CARGO_ARGS ?= --workspace
ARGS ?=
RUST_MIN_STACK ?= 8388608

# Targets use <scope>-<action>[-<qualifier>]; repository-wide workflows use bare names.
include Makefile.dev

# =============================================================================
# Repository: aggregate workflows and command discovery.
# =============================================================================
.PHONY: help install build typecheck check test format

# List targets and their purpose; pass tool flags with ARGS or CARGO_ARGS.
help:
	@awk '/^# / { description = substr($$0, 3); next } /^[a-z][a-z-]*:/ { printf "  %-25s %s\n", $$1, description }' $(MAKEFILE_LIST)

# Install locked Node packages and fetch the pinned Rust workspace dependencies.
install: node-install rust-install

# Build native tools, frontend assets, test tooling, and the .NET solution in order.
build: rust-build frontend-build tools-build backend-build

# Check generated contracts, browser source, Node tooling, and the .NET build.
typecheck: contracts-check scripts-typecheck
	npm --prefix frontend run typecheck
	tsc -p tsconfig.json --noEmit
	$(MAKE) backend-build

# Run formatting checks, Rust lints, the full offline suite, and type checks.
check: rust-format-check rust-lint test typecheck

# Preserve all pretest checks, builds, generators, and offline application tests.
test: contracts-check contracts-test make-test ci-test maintenance-test chisel-check dependencies-check build deploy-generate rust-test release-test protocol-check kubernetes-check backend-test node-test

# Format TypeScript, Rust, and handwritten C# using the existing conventions.
format: typescript-format rust-format backend-format

# =============================================================================
# Artifacts: generated output cleanup.
# =============================================================================
.PHONY: artifacts-clean

# Recursively delete everything in .artifacts except its root .gitkeep.
artifacts-clean:
	@find .artifacts -mindepth 1 -maxdepth 1 ! -name .gitkeep -exec rm -rf -- {} +
	@printf 'Cleaned .artifacts (kept .gitkeep).\n'

# =============================================================================
# Backend: .NET builds, tests, and handwritten C# formatting.
# =============================================================================
.PHONY: backend-build backend-test backend-format

# Build the .NET solution using the existing frontend output.
backend-build:
	dotnet build backend/Goblin.slnx --nologo

# Run .NET tests after building the backend; real database checks remain opt-in.
backend-test: backend-build
	dotnet test backend/Goblin.slnx --no-build --nologo $(ARGS)

# Format handwritten C# while excluding generated protocol and EF files.
backend-format:
	cd backend && dotnet format whitespace Goblin.slnx --exclude src/Goblin.Protocol/Generated src/Goblin.Persistence/Generated
	cd backend && dotnet format style Goblin.slnx --severity info --exclude-diagnostics IDE0130 IDE1006 --exclude src/Goblin.Protocol/Generated src/Goblin.Persistence/Generated

# =============================================================================
# Browser: Playwright installation and application journeys.
# =============================================================================
.PHONY: browser-install browser-test

# Install Playwright's pinned Chromium revision and its operating-system libraries.
browser-install:
	playwright install --with-deps chromium

# Build and run Playwright journeys; ARGS selects tests or browser options.
browser-test: build
	playwright test $(ARGS)

# =============================================================================
# CI: change selection and required-result policy tests.
# =============================================================================
.PHONY: ci-test

# Verify CI change selection, Git comparisons, summaries, and required-result policy.
ci-test:
	node --test scripts/ci.test.mts

# =============================================================================
# Codex: runtime integration checks.
# =============================================================================
.PHONY: codex-test

# Build and check the pinned Codex runtime using isolated test credentials.
codex-test: build
	node dist/scripts/check-codex.js

# =============================================================================
# Contracts: browser value generation and validation.
# =============================================================================
.PHONY: contracts-generate contracts-check contracts-test

# Regenerate browser contract values from owning C# and Rust definitions.
contracts-generate:
	node scripts/generate-contract-values.mts

# Reject stale generated browser contract values without writing files.
contracts-check:
	node scripts/generate-contract-values.mts --check

# Test contract generation, including rejection of stale or invalid definitions.
contracts-test:
	node --test scripts/generate-contract-values.test.mts

# =============================================================================
# Dependencies: locked deployment catalog checks and resolution.
# =============================================================================
.PHONY: dependencies-check dependencies-resolve

# Verify the locked deployment dependency catalog and generated references.
dependencies-check:
	cargo xtask dependencies check --locked

# Resolve deployment dependencies using the existing explicit update workflow.
dependencies-resolve:
	cargo xtask dependencies resolve $(ARGS)

# =============================================================================
# Deploy: offline Azure review asset generation.
# =============================================================================
.PHONY: deploy-generate

# Generate Azure review assets offline using the pinned standalone Bicep CLI.
deploy-generate:
	cargo xtask azure

# =============================================================================
# Frontend: browser asset compilation and contract tests.
# =============================================================================
.PHONY: frontend-build frontend-test

# Delegate browser asset compilation to the frontend npm package.
frontend-build:
	npm --prefix frontend run build

# Build Node test tooling and run the frontend contract tests.
frontend-test: tools-build
	node --test dist/tests/frontend/*.test.js

# =============================================================================
# Headlamp: configured service integration checks.
# =============================================================================
.PHONY: headlamp-test

# Build and check a configured real Headlamp service.
headlamp-test: build
	node dist/scripts/check-headlamp.js

# =============================================================================
# Kubernetes: pinned model generation and validation.
# =============================================================================
.PHONY: kubernetes-generate kubernetes-check

# Regenerate selected Kubernetes and Agent Sandbox models from pinned schemas.
kubernetes-generate:
	dotnet run --file backend/scripts/GenerateKubernetes.cs

# Run Kubernetes generator self-tests and reject stale generated models.
kubernetes-check:
	dotnet run --file backend/scripts/GenerateKubernetes.cs -- --self-test --check

# =============================================================================
# Logging: disposable-cluster smoke tests and configured UI checks.
# =============================================================================
.PHONY: logging-test logging-test-ui

# Build and run the existing disposable-cluster logging smoke test.
logging-test: build
	bash scripts/check-logging.sh

# Build and check a configured real VictoriaLogs UI.
logging-test-ui: build
	node dist/scripts/check-logs-ui.js

# =============================================================================
# Maintenance: scoped agentic workflow tests.
# =============================================================================
.PHONY: maintenance-test

# Verify owner-only agentic maintenance, focused context, and scoped patch validation.
maintenance-test:
	node --test scripts/agentic-maintenance.test.mts

# =============================================================================
# Chisel: offline manifest and deployment credential verification.
# =============================================================================
.PHONY: chisel-check

# Verify the self-hosted WSL tunnel deployment without contacting Kubernetes.
chisel-check:
	node --test scripts/chisel.test.mts

# =============================================================================
# Make: task runner workflow tests.
# =============================================================================
.PHONY: make-test

# Verify Make workflow ordering, failure propagation, and explicit flag forwarding.
make-test:
	node --test scripts/make-workflows.test.mts

# =============================================================================
# Node: locked tooling installation and compiled JavaScript tests.
# =============================================================================
.PHONY: node-install node-test

# Install repository tooling and frontend packages from their separate lockfiles.
node-install:
	npm ci
	npm --prefix frontend ci

# Run compiled frontend, HTTP integration, and deployment contract tests.
node-test: tools-build
	node --test dist/tests/frontend/*.test.js dist/tests/integration/*.test.js dist/tests/deployment/*.test.js

# =============================================================================
# Platform: installer image build checks.
# =============================================================================
.PHONY: platform-check-image

# Build the platform image the installer builds, catching missing Docker inputs.
platform-check-image:
	docker build --platform linux/amd64 --progress=plain -t goblin-platform:check .

# =============================================================================
# Protocol: Codex model generation and validation.
# =============================================================================
.PHONY: protocol-generate protocol-check

# Regenerate Codex protocol models from the checked-in schemas and selection.
protocol-generate:
	dotnet run --file backend/scripts/GenerateProtocol.cs

# Run protocol generator self-tests and reject stale generated models.
protocol-check:
	dotnet run --file backend/scripts/GenerateProtocol.cs -- --self-test --check

# =============================================================================
# Release: native builds, installer checks, local packaging, and tests.
# =============================================================================
.PHONY: release-build release-check-installer release-package-local release-test

# Build the release CLI through the shared fingerprinted build recipe.
release-build:
	bash scripts/build-goblinctl-release.sh target

# Check installer contracts and release compiler-input coverage after a build.
release-check-installer:
	cargo xtask release check-installer

# Build and package the Linux musl operator CLI for local review.
release-package-local: release-build
	cargo xtask package --binary target/x86_64-unknown-linux-musl/release/goblinctl

# Run release tooling and publication contract tests directly with Node.
release-test:
	node --test scripts/release-*.test.mts scripts/installation-assets.test.mts

# =============================================================================
# Rust: pinned toolchain setup, builds, formatting, linting, and tests.
# =============================================================================
.PHONY: rust-setup rust-install rust-build rust-format rust-format-check rust-lint rust-fix rust-test rust-test-doc

# Install the pinned Rust compiler and development helpers as the current user.
rust-setup:
	bash scripts/setup-rust.sh

# Load the pinned Rust toolchain and fetch locked workspace dependencies.
rust-install:
	rustup show active-toolchain
	cargo fetch --locked

# Build Rust tools; override CARGO_ARGS to select a crate or target.
rust-build:
	cargo build --locked $(CARGO_ARGS)

# Format Rust with one imported item per use statement.
rust-format:
	cargo fmt --all -- --config imports_granularity=Item

# Check Rust formatting without writing files.
rust-format-check:
	cargo fmt --all -- --config imports_granularity=Item --check

# Lint selected Rust crates and all targets with warnings denied.
rust-lint:
	cargo clippy --locked --tests $(CARGO_ARGS) --all-targets -- -D warnings $(ARGS)

# Apply Clippy fixes to selected Rust crates and tests; review the resulting diff.
rust-fix:
	cargo clippy --locked --fix --tests --allow-dirty $(CARGO_ARGS) $(ARGS)

# Run nextest with the existing local profile and stack size, then doc tests.
rust-test:
	RUST_MIN_STACK=$(RUST_MIN_STACK) NEXTEST_PROFILE=local cargo nextest run --locked --no-fail-fast $(CARGO_ARGS) $(ARGS)
	$(MAKE) rust-test-doc

# Run Rust documentation tests, which nextest does not execute.
rust-test-doc:
	cargo test --locked --doc $(CARGO_ARGS)

# =============================================================================
# Scripts: directly executed TypeScript checks.
# =============================================================================
.PHONY: scripts-typecheck

# Check directly executed TypeScript scripts without emitting files.
scripts-typecheck:
	tsc -p tsconfig.scripts.json

# =============================================================================
# Secrets: hook setup and working-tree, staged, and history scans.
# =============================================================================
.PHONY: secrets-setup secrets-scan secrets-scan-staged secrets-scan-history secrets-test

# Install secret-scanning hooks without replacing an existing hook configuration.
secrets-setup:
	node scripts/install-git-hooks.mts

# Scan the index and working tree with the existing Gitleaks safety rules.
secrets-scan:
	node scripts/check-secrets.mts

# Scan staged file contents before committing.
secrets-scan-staged:
	node scripts/check-secrets.mts staged

# Scan all reachable commit history before pushing.
secrets-scan-history:
	node scripts/check-secrets.mts history

# Test secret scanning and Git hooks with the installed Gitleaks CLI.
secrets-test:
	node --test scripts/check-secrets.test.mts

# =============================================================================
# Tools: compiled Node test tooling and native host fixtures.
# =============================================================================
.PHONY: tools-build

# Clean compiled test tooling, check direct Node scripts, and compile test tools.
tools-build:
	cargo build --locked -p goblinctl --example host_fixture
	node scripts/clean-output.mts
	$(MAKE) scripts-typecheck
	tsc -p tsconfig.json

# =============================================================================
# TypeScript: repository source formatting.
# =============================================================================
.PHONY: typescript-format

# Format repository TypeScript while respecting ignored files and embedded text.
typescript-format:
	prettier --write --ignore-path .gitignore "**/*.{ts,tsx,mts,cts}"
