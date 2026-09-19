-- Cleanup is durable work for the host, and never permission to execute again.
ALTER TABLE goblin.execution_attempts
    ADD COLUMN cleanup_pending boolean NOT NULL DEFAULT false,
    ADD COLUMN cleanup_failed boolean NOT NULL DEFAULT false;
DROP INDEX goblin.one_execution_per_connection;
CREATE UNIQUE INDEX one_execution_per_connection ON goblin.execution_attempts(connection_id)
WHERE status IN ('Starting', 'Running', 'CancellationRequested', 'Uncertain') OR cleanup_pending;
