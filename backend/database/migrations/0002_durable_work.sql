-- Goblin owns identities and history. Runtime sessions are opaque references.
CREATE TABLE goblin.connections (
    id uuid PRIMARY KEY,
    runtime text NOT NULL,
    name text NOT NULL,
    availability text NOT NULL DEFAULT 'Disconnected',
    changed_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE goblin.agents (
    id uuid PRIMARY KEY,
    name text NOT NULL,
    connection_id uuid NOT NULL REFERENCES goblin.connections(id),
    model text
);
INSERT INTO goblin.connections (id, runtime, name)
VALUES ('00000000-0000-0000-0000-000000000001', 'codex', 'Codex');
INSERT INTO goblin.agents (id, name, connection_id)
VALUES ('00000000-0000-0000-0000-000000000001', 'Goblin', '00000000-0000-0000-0000-000000000001');

ALTER TABLE goblin.work_items
    ADD COLUMN state jsonb,
    ADD COLUMN version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    ADD COLUMN status text NOT NULL DEFAULT 'Ready',
    ADD COLUMN agent_id uuid REFERENCES goblin.agents(id),
    ADD COLUMN created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now();
CREATE INDEX work_items_updated ON goblin.work_items(updated_at DESC, id);

-- Projection used for capacity, ownership, and recovery queries. The immutable
-- attempt data and ordered product history also live in the Work snapshot.
CREATE TABLE goblin.execution_attempts (
    id uuid PRIMARY KEY,
    work_id uuid NOT NULL REFERENCES goblin.work_items(id),
    agent_id uuid NOT NULL REFERENCES goblin.agents(id),
    connection_id uuid NOT NULL REFERENCES goblin.connections(id),
    runtime text NOT NULL,
    status text NOT NULL,
    owner_id uuid,
    environment_reference text,
    queued_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);
CREATE INDEX attempts_recovery ON goblin.execution_attempts(status, updated_at);
-- A connection's mutable credentials may serve only one active attempt.
CREATE UNIQUE INDEX one_execution_per_connection ON goblin.execution_attempts(connection_id)
WHERE status IN ('Starting', 'Running', 'CancellationRequested', 'Uncertain');

-- Retain receipts so browser reconnects/repeated POSTs cannot repeat commands.
CREATE TABLE goblin.work_commands (
    id uuid PRIMARY KEY,
    work_id uuid NOT NULL REFERENCES goblin.work_items(id),
    fingerprint text NOT NULL,
    response jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE goblin.conversations (
    id uuid PRIMARY KEY,
    title text NOT NULL,
    work_id uuid REFERENCES goblin.work_items(id),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE goblin.conversation_messages (
    id uuid PRIMARY KEY,
    conversation_id uuid NOT NULL REFERENCES goblin.conversations(id),
    body text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX conversation_history ON goblin.conversation_messages(conversation_id, created_at, id);
