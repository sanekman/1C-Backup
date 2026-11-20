CREATE TABLE server_remote_runs (
    command_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES server_agents(id),
    generation bytea NOT NULL CHECK (octet_length(generation)=32),
    job_id uuid NOT NULL,
    revision bigint NOT NULL CHECK (revision>0),
    lifetime_minutes integer NOT NULL CHECK (lifetime_minutes BETWEEN 1 AND 1440),
    expires_utc timestamptz NOT NULL,
    status varchar(16) NOT NULL DEFAULT 'Queued' CHECK (status IN ('Queued','Accepted','Running','Succeeded','Failed','Rejected','Expired','Interrupted')),
    run_id uuid, reason varchar(40),
    created_utc timestamptz NOT NULL DEFAULT now(), updated_utc timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX server_remote_runs_agent ON server_remote_runs(agent_id,created_utc DESC);
INSERT INTO server_schema(version) VALUES(6);
