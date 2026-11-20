CREATE TABLE server_configuration_history (
    agent_id uuid NOT NULL REFERENCES server_agents(id),
    revision bigint NOT NULL CHECK (revision>=1),
    document jsonb NOT NULL,
    saved_utc timestamptz NOT NULL DEFAULT now(),
    author_id uuid REFERENCES server_users(id),
    restored_from bigint CHECK (restored_from>=1),
    PRIMARY KEY(agent_id,revision)
);
INSERT INTO server_configuration_history(agent_id,revision,document,saved_utc)
    SELECT agent_id,revision,document,created_utc FROM server_configurations;
INSERT INTO server_schema(version) VALUES(4);
