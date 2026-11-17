CREATE INDEX ix_cloud_sync_events_store_id_cursor
    ON cloud_sync_events(store_id, id);
