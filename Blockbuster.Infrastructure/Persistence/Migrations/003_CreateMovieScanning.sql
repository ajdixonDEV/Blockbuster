CREATE TABLE configured_library_scan_state
(
    library_source_id TEXT NOT NULL,
    root_path TEXT NOT NULL,
    last_started_at TEXT NULL,
    last_completed_at TEXT NULL,
    last_succeeded INTEGER NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    PRIMARY KEY (library_source_id, root_path)
);

CREATE TABLE library_scan_runs
(
    id TEXT NOT NULL PRIMARY KEY,
    library_source_id TEXT NOT NULL,
    root_path TEXT NOT NULL,
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    succeeded INTEGER NOT NULL DEFAULT 0,
    discovered_files INTEGER NOT NULL DEFAULT 0,
    changed_files INTEGER NOT NULL DEFAULT 0,
    missing_files INTEGER NOT NULL DEFAULT 0,
    error TEXT NULL
);
CREATE INDEX ix_library_scan_runs_started ON library_scan_runs(started_at DESC);

CREATE TABLE media_files
(
    id TEXT NOT NULL PRIMARY KEY,
    library_source_id TEXT NOT NULL,
    root_path TEXT NOT NULL,
    media_kind INTEGER NOT NULL,
    relative_path TEXT NOT NULL,
    normalized_relative_path TEXT NOT NULL,
    length INTEGER NOT NULL,
    last_modified_at TEXT NOT NULL,
    duration_seconds REAL NULL,
    container TEXT NULL,
    video_codec TEXT NULL,
    audio_codec TEXT NULL,
    width INTEGER NULL,
    height INTEGER NULL,
    audio_channels INTEGER NULL,
    probe_error TEXT NULL,
    is_available INTEGER NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    CONSTRAINT ux_media_files_source_path UNIQUE (library_source_id, root_path, normalized_relative_path)
);
CREATE INDEX ix_media_files_available ON media_files(is_available, media_kind);

CREATE TABLE movies
(
    id TEXT NOT NULL PRIMARY KEY,
    tmdb_id INTEGER NULL,
    provider_title TEXT NOT NULL,
    original_title TEXT NULL,
    provider_year INTEGER NULL,
    overview TEXT NULL,
    runtime_seconds REAL NULL,
    poster_provider_path TEXT NULL,
    backdrop_provider_path TEXT NULL,
    local_poster_path TEXT NULL,
    local_backdrop_path TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX ux_movies_tmdb_id ON movies(tmdb_id) WHERE tmdb_id IS NOT NULL;

CREATE TABLE movie_overrides
(
    movie_id TEXT NOT NULL PRIMARY KEY REFERENCES movies(id) ON DELETE CASCADE,
    title TEXT NULL,
    year INTEGER NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE movie_genres
(
    movie_id TEXT NOT NULL REFERENCES movies(id) ON DELETE CASCADE,
    genre TEXT NOT NULL,
    PRIMARY KEY (movie_id, genre)
);

CREATE TABLE movie_versions
(
    movie_id TEXT NOT NULL REFERENCES movies(id) ON DELETE CASCADE,
    media_file_id TEXT NOT NULL UNIQUE REFERENCES media_files(id) ON DELETE CASCADE,
    PRIMARY KEY (movie_id, media_file_id)
);

CREATE TABLE pending_movie_matches
(
    media_file_id TEXT NOT NULL PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    parsed_title TEXT NOT NULL,
    parsed_year INTEGER NULL,
    outcome INTEGER NOT NULL,
    explanation TEXT NOT NULL,
    candidates_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
