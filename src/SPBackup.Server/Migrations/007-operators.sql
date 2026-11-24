ALTER TABLE server_users
    ADD COLUMN role text NOT NULL DEFAULT 'Administrator' CHECK (role IN ('Administrator','Operator')),
    ADD COLUMN enabled boolean NOT NULL DEFAULT true;

CREATE TABLE server_operator_grants (
    user_id uuid NOT NULL REFERENCES server_users(id),
    agent_id uuid NOT NULL REFERENCES server_agents(id),
    -- Empty uuid grants the entire agent, including jobs added later.
    job_id uuid NOT NULL,
    PRIMARY KEY(user_id,agent_id,job_id)
);
INSERT INTO server_schema(version) VALUES(7);
