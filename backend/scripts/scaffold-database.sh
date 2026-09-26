#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
dotnet tool restore
# Select product tables explicitly: public also holds Wolverine's tables and
# the administrator-only migration journal, which are not part of this model.
dotnet ef dbcontext scaffold Name=ConnectionStrings:Goblin Npgsql.EntityFrameworkCore.PostgreSQL \
  --project backend/src/Goblin.Persistence \
  --startup-project backend/tools/Goblin.Database \
  --context GoblinDbContext \
  --context-dir Generated \
  --output-dir Generated/Entities \
  --context-namespace Goblin.Persistence \
  --namespace Goblin.Persistence.Entities \
  --table public.agents \
  --table public.connections \
  --table public.connection_model_catalogs \
  --table public.conversation_messages \
  --table public.conversations \
  --table public.execution_attempts \
  --table public.github_connections \
  --table public.github_repositories \
  --table public.repository_operations \
  --table public.repository_setup_memories \
  --table public.work_commands \
  --table public.work_items \
  --table public.workspace_checkpoints \
  --table public.workspace_sessions \
  --data-annotations \
  --no-onconfiguring \
  --force

cargo xtask normalize-ef

# Apply repository style preferences without converting regular constructors.
dotnet format whitespace backend/src/Goblin.Persistence/Goblin.Persistence.csproj \
  --no-restore --include-generated
dotnet format style backend/src/Goblin.Persistence/Goblin.Persistence.csproj \
  --no-restore --include-generated --severity info --exclude-diagnostics IDE0130 IDE1006

printf 'Review Generated/ for schema changes, including obsolete entity files after table removal or renaming.\n'
