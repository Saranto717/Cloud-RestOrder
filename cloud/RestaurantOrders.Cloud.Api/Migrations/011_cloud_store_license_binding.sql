ALTER TABLE cloud_stores
    ADD COLUMN license_id uuid NULL REFERENCES cloud_licenses(id) ON DELETE RESTRICT;

CREATE UNIQUE INDEX ux_cloud_stores_license
    ON cloud_stores(license_id)
    WHERE license_id IS NOT NULL;
