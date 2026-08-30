-- Run-scoped observations are intentionally separate from the live catalog.
-- A reconciler may discard these rows on failure without changing availability.
CREATE TABLE library_scan_observations
(
    run_id TEXT NOT NULL REFERENCES library_scan_runs(id) ON DELETE CASCADE,
    normalized_relative_path TEXT NOT NULL,
    relative_path TEXT NOT NULL,
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
    assigned_media_file_id TEXT NULL,
    match_resolution_json TEXT NULL,
    PRIMARY KEY (run_id, normalized_relative_path)
);
CREATE INDEX ix_library_scan_observations_run ON library_scan_observations(run_id);
