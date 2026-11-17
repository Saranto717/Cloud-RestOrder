DROP INDEX IF EXISTS ix_cloud_pos_users_store_email;
CREATE UNIQUE INDEX ux_cloud_pos_users_email ON cloud_pos_users(lower(email));

CREATE TABLE cloud_license_request_nonces (
    installation_id uuid NOT NULL,
    nonce varchar(128) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    PRIMARY KEY (installation_id, nonce)
);
CREATE INDEX ix_cloud_license_request_nonces_created ON cloud_license_request_nonces(created_at_utc);

DELETE FROM cloud_license_request_nonces WHERE created_at_utc < now() - interval '10 minutes';

CREATE TABLE cloud_license_manager_sessions (
    token_sha256 bytea PRIMARY KEY,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    last_activity_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    expires_at_utc timestamp with time zone NOT NULL,
    revoked_at_utc timestamp with time zone NULL
);
CREATE INDEX ix_cloud_license_manager_sessions_expiry ON cloud_license_manager_sessions(expires_at_utc);

ALTER TABLE cloud_license_installations
    ADD COLUMN request_public_key_sha256 bytea NULL;
