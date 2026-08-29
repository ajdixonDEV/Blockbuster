CREATE TABLE administrator_credential
(
    singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
    pin_hash TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE profiles
(
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE,
    pin_hash TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CONSTRAINT profiles_name_not_blank CHECK (length(trim(name)) > 0)
);

CREATE UNIQUE INDEX ux_profiles_name ON profiles(name COLLATE NOCASE);
