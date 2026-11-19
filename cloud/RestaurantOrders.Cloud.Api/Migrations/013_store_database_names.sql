ALTER TABLE cloud_stores ADD COLUMN IF NOT EXISTS database_name varchar(63);

-- Existing stores keep their current physical database names. New stores
-- receive a shop-name based name when the store is created.
UPDATE cloud_stores
SET database_name = 'restaurant_store_' || replace(id::text, '-', '')
WHERE database_name IS NULL;

ALTER TABLE cloud_stores ALTER COLUMN database_name SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_cloud_stores_database_name ON cloud_stores(database_name);
