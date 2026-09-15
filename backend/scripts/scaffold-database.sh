#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
dotnet tool restore
dotnet ef dbcontext scaffold Name=ConnectionStrings:Goblin Npgsql.EntityFrameworkCore.PostgreSQL \
  --project backend/src/Goblin.Persistence \
  --startup-project backend/tools/Goblin.Database \
  --context GoblinDbContext \
  --context-dir Generated \
  --output-dir Generated/Entities \
  --context-namespace Goblin.Persistence \
  --namespace Goblin.Persistence.Entities \
  --schema goblin \
  --data-annotations \
  --no-onconfiguring \
  --force

python3 - <<'PY'
from pathlib import Path

for path in Path("backend/src/Goblin.Persistence/Generated").rglob("*.cs"):
    # EF's templates include CRLF and a BOM on some platforms. Keep checked-in
    # output consistent with the rest of the repository.
    text = path.read_text(encoding="utf-8-sig")
    path.write_text(text, encoding="utf-8", newline="\n")
PY

printf 'Review Generated/ for schema changes, including obsolete entity files after table removal or renaming.\n'
