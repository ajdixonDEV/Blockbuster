CREATE TABLE movie_progress
(
    profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    movie_id TEXT NOT NULL REFERENCES movies(id) ON DELETE CASCADE,
    position_seconds REAL NOT NULL,
    revision INTEGER NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (profile_id, movie_id)
);

CREATE TABLE playback_events
(
    id TEXT NOT NULL PRIMARY KEY,
    profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    movie_id TEXT NOT NULL REFERENCES movies(id) ON DELETE CASCADE,
    event_type TEXT NOT NULL,
    position_seconds REAL NOT NULL,
    occurred_at TEXT NOT NULL
);
CREATE INDEX ix_playback_events_profile_time ON playback_events(profile_id, occurred_at DESC);
