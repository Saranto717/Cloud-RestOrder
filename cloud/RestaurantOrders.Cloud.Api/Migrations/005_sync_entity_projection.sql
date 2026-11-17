CREATE TABLE cloud_sync_entities (
    store_id uuid NOT NULL REFERENCES cloud_stores(id) ON DELETE RESTRICT,
    entity_type varchar(80) NOT NULL,
    entity_id varchar(160) NOT NULL,
    cursor bigint NOT NULL,
    is_deleted boolean NOT NULL DEFAULT FALSE,
    schema_version integer NOT NULL CHECK (schema_version BETWEEN 1 AND 1000),
    updated_at_utc timestamp with time zone NOT NULL,
    payload jsonb NOT NULL,
    PRIMARY KEY (store_id, entity_type, entity_id)
);

CREATE INDEX ix_cloud_sync_entities_store_cursor
    ON cloud_sync_entities(store_id, cursor);
