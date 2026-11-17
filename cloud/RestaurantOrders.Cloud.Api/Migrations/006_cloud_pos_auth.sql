CREATE TABLE cloud_pos_users (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    store_id uuid NOT NULL REFERENCES cloud_stores(id) ON DELETE CASCADE,
    email varchar(254) NOT NULL,
    password_hash varchar(240) NOT NULL,
    role varchar(16) NOT NULL CHECK (role IN ('admin', 'waiter')),
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ix_cloud_pos_users_store_email
    ON cloud_pos_users(store_id, lower(email));

CREATE TABLE cloud_pos_sessions (
    token_sha256 bytea PRIMARY KEY,
    store_id uuid NOT NULL REFERENCES cloud_stores(id) ON DELETE CASCADE,
    user_id integer NOT NULL REFERENCES cloud_pos_users(id) ON DELETE CASCADE,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    last_activity_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    expires_at_utc timestamp with time zone NOT NULL,
    revoked_at_utc timestamp with time zone NULL
);
CREATE INDEX ix_cloud_pos_sessions_expiry ON cloud_pos_sessions(expires_at_utc);

CREATE TABLE cloud_pos_commands (
    store_id uuid NOT NULL REFERENCES cloud_stores(id) ON DELETE CASCADE,
    request_id uuid NOT NULL,
    user_id integer NOT NULL REFERENCES cloud_pos_users(id) ON DELETE RESTRICT,
    entity_type varchar(40) NOT NULL,
    entity_id varchar(40) NULL,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    PRIMARY KEY (store_id, request_id)
);

CREATE SEQUENCE cloud_pos_entity_id_seq AS bigint
    START WITH -1 INCREMENT BY -1 MINVALUE -2147483648 MAXVALUE -1 NO CYCLE;
