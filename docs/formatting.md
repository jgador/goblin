# Formatting

The repository-local Codex `Stop` hook in `.codex/config.toml` formats TypeScript,
Python, and C# after each completed turn, including question-only turns. Restart
Codex and use `/hooks` to review and trust the hook after changing its definition.
The project must also be trusted for Codex to load its local configuration.

## Install the tools

From the repository root on Linux, macOS, or WSL:

```bash
npm ci
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements-dev.txt
```

On Debian/Ubuntu, install `python3-venv` if creating the environment reports that
`ensurepip` is unavailable. Prettier is pinned in `package-lock.json`; Ruff is
pinned in `requirements-dev.txt`. `.venv/` and Ruff's cache are ignored by Git.
The hook uses these installed tools and does not install packages during a turn.
The C# commands require the .NET SDK specified in `global.json`.

Also install the [Bicep CLI](https://learn.microsoft.com/azure/azure-resource-manager/bicep/install)
on `PATH`, or place its standalone executable at `.venv/bin/bicep`. It is needed
when Python formatting changes `deploy/azure/setup/__main__.py` or
`deploy/azure/hash-password.py`, or their deployment artifacts are already stale.
The Python formatter checks this prerequisite before editing files, then rebuilds
the setup bundle and both ARM templates through the existing generators. A later
run can therefore repair artifacts left stale by an interrupted regeneration.

## Run manually

```bash
npm run format:typescript
npm run format:python
```

Prettier formats `.ts`, `.tsx`, `.mts`, and `.cts` files, reads `.editorconfig`, and
respects `.gitignore`. Embedded-language formatting is disabled to preserve
HTML/CSS/JavaScript strings used by the UI and test fixtures. Ruff formats Python
sources using its standard formatting rules and respects `.gitignore`.

The hook also runs `dotnet format whitespace` and `dotnet format style --severity
info` against `backend/Goblin.slnx`, excluding generated protocol types and EF
mappings. The style pass excludes `IDE0130` and `IDE1006` because the formatter
cannot apply their namespace and naming changes at solution scope, as recorded in
the [generated C# formatting audit](generated-csharp-formatting.md).
All formatters run sequentially; a failure is reported by Codex and
stops the remaining commands. The hook sends tool logs to stderr and returns the
JSON output Codex expects on success. It does not automatically rerun tests after
formatting; review the resulting changes and run the relevant repository checks.
