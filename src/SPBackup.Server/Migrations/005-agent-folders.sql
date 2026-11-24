CREATE TABLE server_agent_folders (
    id uuid PRIMARY KEY CHECK (id <> '00000000-0000-0000-0000-000000000000'),
    parent_id uuid REFERENCES server_agent_folders(id),
    name varchar(128) NOT NULL,
    name_key varchar(128) NOT NULL,
    CHECK (parent_id IS NULL OR parent_id <> id)
);
CREATE UNIQUE INDEX server_agent_folders_sibling_name ON server_agent_folders
    (coalesce(parent_id,'00000000-0000-0000-0000-000000000000'::uuid), name_key);
ALTER TABLE server_agents ADD COLUMN computer_name varchar(128);
UPDATE server_agents SET computer_name=name;
ALTER TABLE server_agents ADD COLUMN folder_id uuid REFERENCES server_agent_folders(id);
CREATE INDEX server_agents_folder ON server_agents(folder_id);
ALTER TABLE server_invitations ADD COLUMN agent_display_name varchar(128);
ALTER TABLE server_invitations ADD COLUMN folder_id uuid REFERENCES server_agent_folders(id);
ALTER TABLE server_invitations ADD COLUMN organization_configured boolean NOT NULL DEFAULT false;
INSERT INTO server_schema(version) VALUES(5);
