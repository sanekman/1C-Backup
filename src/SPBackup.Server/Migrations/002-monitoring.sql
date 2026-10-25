CREATE TABLE server_monitoring (
    agent_id uuid PRIMARY KEY REFERENCES server_agents(id), received_utc timestamptz NOT NULL DEFAULT now(),
    document jsonb NOT NULL
);
CREATE TABLE server_runs (
    agent_id uuid NOT NULL REFERENCES server_agents(id), run_id uuid NOT NULL, job_id uuid NOT NULL,
    finished_utc timestamptz NOT NULL, received_utc timestamptz NOT NULL DEFAULT now(), document jsonb NOT NULL,
    PRIMARY KEY(agent_id,run_id)
);
CREATE INDEX server_runs_history ON server_runs(agent_id,finished_utc DESC,run_id);
INSERT INTO server_schema(version) VALUES(2);
