CREATE TABLE cloud_sync_devices (
    store_id uuid NOT NULL REFERENCES cloud_stores(id) ON DELETE CASCADE,
    installation_id uuid NOT NULL,
    signing_key_sha256 bytea NOT NULL,
    is_active boolean NOT NULL DEFAULT TRUE,
    registered_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    last_seen_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    PRIMARY KEY (store_id, installation_id)
);
CREATE UNIQUE INDEX ux_cloud_sync_devices_installation ON cloud_sync_devices(installation_id);

CREATE TABLE cloud_sync_nonces (
    store_id uuid NOT NULL,
    installation_id uuid NOT NULL,
    nonce varchar(128) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    PRIMARY KEY (store_id, installation_id, nonce),
    FOREIGN KEY (store_id, installation_id)
        REFERENCES cloud_sync_devices(store_id, installation_id) ON DELETE CASCADE
);
CREATE INDEX ix_cloud_sync_nonces_created ON cloud_sync_nonces(created_at_utc);
