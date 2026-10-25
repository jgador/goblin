#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
if [[ "${1:-}" != --configured ]]; then
  exec cargo xtask database-scaffold --profile "${1:-wsl}"
fi

dotnet tool restore
# Select product tables explicitly: public also holds Wolverine's tables and
# the administrator-only migration journal, which are not part of this model.
dotnet ef dbcontext scaffold Name=ConnectionStrings:Goblin Npgsql.EntityFrameworkCore.PostgreSQL \
  --project backend/src/Goblin.Persistence \
  --startup-project backend/tools/Goblin.Database.Scaffolding \
  --context GoblinDbContext \
  --context-dir Generated \
  --output-dir Generated/Entities \
  --context-namespace Goblin.Persistence \
  --namespace Goblin.Persistence.Entities \
  --table public.agent \
  --table public.chatgpt_work_grant \
  --table public.chatgpt_grant_event \
  --table public.connection \
  --table public.connection_model_catalog \
  --table public.conversation_message \
  --table public.conversation \
  --table public.execution_attempt \
  --table public.external_identity \
  --table public.external_link_request \
  --table public.external_conversation \
  --table public.external_message \
  --table public.github_connection \
  --table public.github_repository \
  --table public.repository_operation \
  --table public.repository_setup_memory \
  --table public.work_command \
  --table public.work_item \
  --table public.workspace_checkpoint \
  --table public.workspace_session \
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
