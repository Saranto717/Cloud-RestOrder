ALTER TABLE cloud_license_installations
    ADD COLUMN IF NOT EXISTS rebind_pending boolean NOT NULL DEFAULT false;
