CREATE TABLE server_portable_panel_settings (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    display_name varchar(128) NOT NULL,
    time_zone_id varchar(128) NOT NULL
);
CREATE TABLE server_imported_plans (
    agent_id uuid PRIMARY KEY,
    agent_name varchar(128) NOT NULL,
    folder_id uuid REFERENCES server_agent_folders(id) ON DELETE SET NULL,
    jobs jsonb NOT NULL
);
INSERT INTO server_schema(version) VALUES(9);
