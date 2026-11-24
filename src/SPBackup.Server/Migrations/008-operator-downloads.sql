ALTER TABLE server_operator_grants ADD COLUMN allow_download boolean NOT NULL DEFAULT false;
INSERT INTO server_schema(version) VALUES(8);
