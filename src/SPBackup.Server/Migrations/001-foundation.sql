CREATE TABLE server_users (
    id uuid PRIMARY KEY, name varchar(80) NOT NULL UNIQUE,
    password_hash text NOT NULL, security_stamp uuid NOT NULL
);
CREATE TABLE server_agents (
    id uuid PRIMARY KEY, name varchar(128) NOT NULL, registered_utc timestamptz NOT NULL DEFAULT now(),
    token_hash bytea UNIQUE, revoked boolean NOT NULL DEFAULT false,
    received_utc timestamptz, sent_utc timestamptz, agent_version varchar(128),
    execution_available boolean NOT NULL DEFAULT false, reported_revision bigint,
    CHECK (token_hash IS NULL OR octet_length(token_hash) = 32),
    CHECK (reported_revision IS NULL OR reported_revision >= 1)
);
CREATE TABLE server_configurations (
    agent_id uuid PRIMARY KEY REFERENCES server_agents(id), revision bigint NOT NULL CHECK (revision >= 1),
    document jsonb NOT NULL, created_utc timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE server_invitations (
    id uuid PRIMARY KEY, name varchar(128) NOT NULL, agent_id uuid,
    token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
    expires_utc timestamptz NOT NULL, consumed_utc timestamptz,
    created_by uuid NOT NULL REFERENCES server_users(id), created_utc timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE server_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    received_utc timestamptz NOT NULL DEFAULT now(), user_id uuid REFERENCES server_users(id),
    agent_id uuid, event varchar(48) NOT NULL
);
CREATE INDEX server_agents_received ON server_agents(received_utc);
CREATE INDEX server_invitations_expiry ON server_invitations(expires_utc);
CREATE INDEX server_audit_received ON server_audit(received_utc);
