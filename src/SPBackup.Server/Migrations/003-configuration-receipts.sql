CREATE TABLE server_configuration_offers (
    offer_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES server_agents(id),
    generation bytea NOT NULL CHECK (octet_length(generation)=32),
    revision bigint NOT NULL CHECK (revision>=1),
    document_hash bytea NOT NULL CHECK (octet_length(document_hash)=32),
    created_utc timestamptz NOT NULL DEFAULT now(),
    status varchar(16), reason varchar(40), observed_utc timestamptz, received_utc timestamptz,
    UNIQUE(agent_id,generation,revision),
    CHECK (status IS NULL OR status IN ('Accepted','Rejected')),
    CHECK ((status IS NULL AND reason IS NULL AND observed_utc IS NULL AND received_utc IS NULL)
        OR (status IS NOT NULL AND ((status='Accepted' AND reason IS NULL AND observed_utc IS NOT NULL AND received_utc IS NOT NULL)
        OR (status='Rejected' AND reason IS NOT NULL AND reason IN ('InvalidConfiguration','CacheWriteFailed')
            AND observed_utc IS NOT NULL AND received_utc IS NOT NULL))))
);
INSERT INTO server_schema(version) VALUES(3);
