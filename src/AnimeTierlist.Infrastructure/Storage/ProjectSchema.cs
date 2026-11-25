namespace AnimeTierlist.Infrastructure.Storage;

/// <summary>
/// Version 3 of the project database. Constraints enforce identity, ordering, duplicate board copies, cached
/// on-demand anime details/videos, and reference invariants; domain constructors re-validate on load.
/// Newer schema versions are rejected, never guessed.
/// </summary>
internal static class ProjectSchema
{
    public const int Version = 3;

    public const string ApplicationId = "AnimeTierlist.Project";

    /// <summary>Exact schema version 1 DDL used to validate version-1 project archives before staging upgrade.</summary>
    public const string CreateV1 = """
        CREATE TABLE project_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) STRICT;

        CREATE TABLE items (
            livechart_id INTEGER PRIMARY KEY CHECK (livechart_id > 0)
        ) STRICT;

        CREATE TABLE snapshots (
            id       TEXT PRIMARY KEY CHECK (length(id) = 36),
            year     INTEGER NOT NULL CHECK (year BETWEEN 1900 AND 2200),
            season   INTEGER CHECK (season BETWEEN 0 AND 3),
            category INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3)
        ) STRICT;

        CREATE TABLE season_captures (
            snapshot_id    TEXT NOT NULL REFERENCES snapshots (id),
            year           INTEGER NOT NULL,
            season         INTEGER NOT NULL CHECK (season BETWEEN 0 AND 3),
            category       INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3),
            request_url    TEXT NOT NULL CHECK (request_url LIKE 'https://%'),
            retrieved_at   TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (snapshot_id, year, season)
        ) STRICT;

        CREATE TABLE occurrences (
            snapshot_id     TEXT NOT NULL,
            year            INTEGER NOT NULL,
            season          INTEGER NOT NULL,
            ordinal         INTEGER NOT NULL CHECK (ordinal >= 0),
            livechart_id    INTEGER NOT NULL REFERENCES items (livechart_id),
            displayed_title TEXT NOT NULL CHECK (length(trim(displayed_title)) > 0),
            english_title   TEXT,
            romanized_title TEXT,
            native_title    TEXT,
            PRIMARY KEY (snapshot_id, year, season, ordinal),
            FOREIGN KEY (snapshot_id, year, season) REFERENCES season_captures (snapshot_id, year, season)
        ) STRICT;

        CREATE TABLE image_candidates (
            snapshot_id TEXT NOT NULL,
            year        INTEGER NOT NULL,
            season      INTEGER NOT NULL,
            ordinal     INTEGER NOT NULL,
            rank        INTEGER NOT NULL CHECK (rank >= 0),
            variant     TEXT NOT NULL,
            url         TEXT NOT NULL CHECK (url LIKE 'https://%'),
            PRIMARY KEY (snapshot_id, year, season, ordinal, rank),
            FOREIGN KEY (snapshot_id, year, season, ordinal) REFERENCES occurrences (snapshot_id, year, season, ordinal)
        ) STRICT;

        CREATE TABLE assets (
            hash          TEXT PRIMARY KEY CHECK (length(hash) = 64 AND hash NOT GLOB '*[^0-9a-f]*'),
            format        TEXT NOT NULL CHECK (format IN ('jpg', 'png', 'webp')),
            width         INTEGER NOT NULL CHECK (width > 0),
            height        INTEGER NOT NULL CHECK (height > 0),
            byte_length   INTEGER NOT NULL CHECK (byte_length > 0),
            relative_path TEXT NOT NULL UNIQUE CHECK (relative_path = 'assets/' || hash || '.' || format)
        ) STRICT;

        CREATE TABLE collections (
            id                  TEXT PRIMARY KEY CHECK (length(id) = 36),
            name                TEXT NOT NULL CHECK (length(trim(name)) > 0),
            year                INTEGER NOT NULL CHECK (year BETWEEN 1900 AND 2200),
            season              INTEGER CHECK (season BETWEEN 0 AND 3),
            category            INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3),
            applied_snapshot_id TEXT NOT NULL REFERENCES snapshots (id),
            title_language      INTEGER NOT NULL CHECK (title_language BETWEEN 0 AND 3)
        ) STRICT;

        CREATE TABLE collection_entries (
            collection_id         TEXT NOT NULL REFERENCES collections (id) ON DELETE CASCADE,
            livechart_id          INTEGER NOT NULL REFERENCES items (livechart_id),
            ordinal               INTEGER NOT NULL CHECK (ordinal >= 0),
            included              INTEGER NOT NULL CHECK (included IN (0, 1)),
            source_missing        INTEGER NOT NULL CHECK (source_missing IN (0, 1)),
            title_override        TEXT CHECK (title_override IS NULL OR length(trim(title_override)) > 0),
            accepted_snapshot_id  TEXT NOT NULL,
            accepted_year         INTEGER NOT NULL,
            accepted_season       INTEGER NOT NULL,
            accepted_ordinal      INTEGER NOT NULL,
            cover_asset_hash      TEXT REFERENCES assets (hash),
            cover_snapshot_id     TEXT,
            cover_year            INTEGER,
            cover_season          INTEGER,
            cover_ordinal         INTEGER,
            cover_requested_url   TEXT,
            cover_final_url       TEXT,
            cover_request_context TEXT,
            PRIMARY KEY (collection_id, livechart_id),
            UNIQUE (collection_id, ordinal),
            FOREIGN KEY (accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal)
                REFERENCES occurrences (snapshot_id, year, season, ordinal),
            FOREIGN KEY (cover_snapshot_id, cover_year, cover_season, cover_ordinal)
                REFERENCES occurrences (snapshot_id, year, season, ordinal),
            CHECK ((cover_asset_hash IS NULL) = (cover_snapshot_id IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_year IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_season IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_ordinal IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_requested_url IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_final_url IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_request_context IS NULL))
        ) STRICT;

        CREATE TABLE boards (
            id            TEXT PRIMARY KEY CHECK (length(id) = 36),
            collection_id TEXT NOT NULL REFERENCES collections (id),
            name          TEXT NOT NULL CHECK (length(trim(name)) > 0)
        ) STRICT;

        CREATE TABLE tiers (
            board_id TEXT NOT NULL REFERENCES boards (id) ON DELETE CASCADE,
            id       TEXT NOT NULL CHECK (length(id) = 36),
            ordinal  INTEGER NOT NULL CHECK (ordinal >= 0),
            name     TEXT NOT NULL CHECK (length(trim(name)) > 0),
            color    TEXT NOT NULL CHECK (length(color) = 7 AND color GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'),
            PRIMARY KEY (board_id, id),
            UNIQUE (board_id, ordinal)
        ) STRICT;

        CREATE TABLE board_entries (
            board_id          TEXT NOT NULL REFERENCES boards (id) ON DELETE CASCADE,
            livechart_id      INTEGER NOT NULL REFERENCES items (livechart_id),
            seed              INTEGER NOT NULL CHECK (seed >= 0),
            pinned_title      TEXT NOT NULL CHECK (length(trim(pinned_title)) > 0),
            pinned_asset_hash TEXT REFERENCES assets (hash),
            tier_id           TEXT,
            tier_position     INTEGER CHECK (tier_position IS NULL OR tier_position >= 0),
            PRIMARY KEY (board_id, livechart_id),
            UNIQUE (board_id, seed),
            UNIQUE (board_id, tier_id, tier_position),
            FOREIGN KEY (board_id, tier_id) REFERENCES tiers (board_id, id),
            CHECK ((tier_id IS NULL) = (tier_position IS NULL))
        ) STRICT;
        """;

    /// <summary>Exact schema version 2 DDL used to validate version-2 project archives before staging upgrade.</summary>
    public const string CreateV2 = """
        CREATE TABLE project_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) STRICT;

        CREATE TABLE items (
            livechart_id INTEGER PRIMARY KEY CHECK (livechart_id > 0)
        ) STRICT;

        CREATE TABLE snapshots (
            id       TEXT PRIMARY KEY CHECK (length(id) = 36),
            year     INTEGER NOT NULL CHECK (year BETWEEN 1900 AND 2200),
            season   INTEGER CHECK (season BETWEEN 0 AND 3),
            category INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3)
        ) STRICT;

        CREATE TABLE season_captures (
            snapshot_id    TEXT NOT NULL REFERENCES snapshots (id),
            year           INTEGER NOT NULL,
            season         INTEGER NOT NULL CHECK (season BETWEEN 0 AND 3),
            category       INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3),
            request_url    TEXT NOT NULL CHECK (request_url LIKE 'https://%'),
            retrieved_at   TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (snapshot_id, year, season)
        ) STRICT;

        CREATE TABLE occurrences (
            snapshot_id     TEXT NOT NULL,
            year            INTEGER NOT NULL,
            season          INTEGER NOT NULL,
            ordinal         INTEGER NOT NULL CHECK (ordinal >= 0),
            livechart_id    INTEGER NOT NULL REFERENCES items (livechart_id),
            displayed_title TEXT NOT NULL CHECK (length(trim(displayed_title)) > 0),
            english_title   TEXT,
            romanized_title TEXT,
            native_title    TEXT,
            rating          TEXT,
            premiere        TEXT,
            metadata        TEXT,
            synopsis        TEXT,
            studios_json    TEXT,
            tags_json       TEXT,
            PRIMARY KEY (snapshot_id, year, season, ordinal),
            FOREIGN KEY (snapshot_id, year, season) REFERENCES season_captures (snapshot_id, year, season)
        ) STRICT;

        CREATE TABLE image_candidates (
            snapshot_id TEXT NOT NULL,
            year        INTEGER NOT NULL,
            season      INTEGER NOT NULL,
            ordinal     INTEGER NOT NULL,
            rank        INTEGER NOT NULL CHECK (rank >= 0),
            variant     TEXT NOT NULL,
            url         TEXT NOT NULL CHECK (url LIKE 'https://%'),
            PRIMARY KEY (snapshot_id, year, season, ordinal, rank),
            FOREIGN KEY (snapshot_id, year, season, ordinal) REFERENCES occurrences (snapshot_id, year, season, ordinal)
        ) STRICT;

        CREATE TABLE assets (
            hash          TEXT PRIMARY KEY CHECK (length(hash) = 64 AND hash NOT GLOB '*[^0-9a-f]*'),
            format        TEXT NOT NULL CHECK (format IN ('jpg', 'png', 'webp')),
            width         INTEGER NOT NULL CHECK (width > 0),
            height        INTEGER NOT NULL CHECK (height > 0),
            byte_length   INTEGER NOT NULL CHECK (byte_length > 0),
            relative_path TEXT NOT NULL UNIQUE CHECK (relative_path = 'assets/' || hash || '.' || format)
        ) STRICT;

        CREATE TABLE collections (
            id                  TEXT PRIMARY KEY CHECK (length(id) = 36),
            name                TEXT NOT NULL CHECK (length(trim(name)) > 0),
            year                INTEGER NOT NULL CHECK (year BETWEEN 1900 AND 2200),
            season              INTEGER CHECK (season BETWEEN 0 AND 3),
            category            INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3),
            applied_snapshot_id TEXT NOT NULL REFERENCES snapshots (id),
            title_language      INTEGER NOT NULL CHECK (title_language BETWEEN 0 AND 3),
            ordinal             INTEGER NOT NULL UNIQUE CHECK (ordinal >= 0)
        ) STRICT;

        CREATE TABLE collection_entries (
            collection_id         TEXT NOT NULL REFERENCES collections (id) ON DELETE CASCADE,
            livechart_id          INTEGER NOT NULL REFERENCES items (livechart_id),
            ordinal               INTEGER NOT NULL CHECK (ordinal >= 0),
            included              INTEGER NOT NULL CHECK (included IN (0, 1)),
            source_missing        INTEGER NOT NULL CHECK (source_missing IN (0, 1)),
            title_override        TEXT CHECK (title_override IS NULL OR length(trim(title_override)) > 0),
            accepted_snapshot_id  TEXT NOT NULL,
            accepted_year         INTEGER NOT NULL,
            accepted_season       INTEGER NOT NULL,
            accepted_ordinal      INTEGER NOT NULL,
            cover_asset_hash      TEXT REFERENCES assets (hash),
            cover_snapshot_id     TEXT,
            cover_year            INTEGER,
            cover_season          INTEGER,
            cover_ordinal         INTEGER,
            cover_requested_url   TEXT,
            cover_final_url       TEXT,
            cover_request_context TEXT,
            PRIMARY KEY (collection_id, livechart_id),
            UNIQUE (collection_id, ordinal),
            FOREIGN KEY (accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal)
                REFERENCES occurrences (snapshot_id, year, season, ordinal),
            FOREIGN KEY (cover_snapshot_id, cover_year, cover_season, cover_ordinal)
                REFERENCES occurrences (snapshot_id, year, season, ordinal),
            CHECK ((cover_asset_hash IS NULL) = (cover_snapshot_id IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_year IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_season IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_ordinal IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_requested_url IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_final_url IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_request_context IS NULL))
        ) STRICT;

        CREATE TABLE boards (
            id            TEXT PRIMARY KEY CHECK (length(id) = 36),
            collection_id TEXT NOT NULL REFERENCES collections (id),
            name          TEXT NOT NULL CHECK (length(trim(name)) > 0),
            ordinal       INTEGER NOT NULL UNIQUE CHECK (ordinal >= 0)
        ) STRICT;

        CREATE TABLE tiers (
            board_id TEXT NOT NULL REFERENCES boards (id) ON DELETE CASCADE,
            id       TEXT NOT NULL CHECK (length(id) = 36),
            ordinal  INTEGER NOT NULL CHECK (ordinal >= 0),
            name     TEXT NOT NULL CHECK (length(trim(name)) > 0),
            color    TEXT NOT NULL CHECK (length(color) = 7 AND color GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'),
            PRIMARY KEY (board_id, id),
            UNIQUE (board_id, ordinal)
        ) STRICT;

        CREATE TABLE board_entries (
            board_id          TEXT NOT NULL REFERENCES boards (id) ON DELETE CASCADE,
            livechart_id      INTEGER NOT NULL REFERENCES items (livechart_id),
            copy_number       INTEGER NOT NULL CHECK (copy_number >= 1),
            seed              INTEGER NOT NULL CHECK (seed >= 0),
            pinned_title      TEXT NOT NULL CHECK (length(trim(pinned_title)) > 0),
            pinned_asset_hash TEXT REFERENCES assets (hash),
            tier_id           TEXT,
            tier_position     INTEGER CHECK (tier_position IS NULL OR tier_position >= 0),
            PRIMARY KEY (board_id, livechart_id, copy_number),
            UNIQUE (board_id, seed),
            UNIQUE (board_id, tier_id, tier_position),
            FOREIGN KEY (board_id, tier_id) REFERENCES tiers (board_id, id),
            CHECK ((tier_id IS NULL) = (tier_position IS NULL))
        ) STRICT;
        """;

    public const string Create = """
        CREATE TABLE project_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) STRICT;

        CREATE TABLE items (
            livechart_id INTEGER PRIMARY KEY CHECK (livechart_id > 0)
        ) STRICT;

        CREATE TABLE snapshots (
            id       TEXT PRIMARY KEY CHECK (length(id) = 36),
            year     INTEGER NOT NULL CHECK (year BETWEEN 1900 AND 2200),
            season   INTEGER CHECK (season BETWEEN 0 AND 3),
            category INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3)
        ) STRICT;

        CREATE TABLE season_captures (
            snapshot_id    TEXT NOT NULL REFERENCES snapshots (id),
            year           INTEGER NOT NULL,
            season         INTEGER NOT NULL CHECK (season BETWEEN 0 AND 3),
            category       INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3),
            request_url    TEXT NOT NULL CHECK (request_url LIKE 'https://%'),
            retrieved_at   TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (snapshot_id, year, season)
        ) STRICT;

        CREATE TABLE occurrences (
            snapshot_id     TEXT NOT NULL,
            year            INTEGER NOT NULL,
            season          INTEGER NOT NULL,
            ordinal         INTEGER NOT NULL CHECK (ordinal >= 0),
            livechart_id    INTEGER NOT NULL REFERENCES items (livechart_id),
            displayed_title TEXT NOT NULL CHECK (length(trim(displayed_title)) > 0),
            english_title   TEXT,
            romanized_title TEXT,
            native_title    TEXT,
            rating          TEXT,
            premiere        TEXT,
            metadata        TEXT,
            synopsis        TEXT,
            studios_json    TEXT,
            tags_json       TEXT,
            anilist_url     TEXT CHECK (anilist_url IS NULL OR anilist_url LIKE 'https://%'),
            myanimelist_url TEXT CHECK (myanimelist_url IS NULL OR myanimelist_url LIKE 'https://%'),
            PRIMARY KEY (snapshot_id, year, season, ordinal),
            FOREIGN KEY (snapshot_id, year, season) REFERENCES season_captures (snapshot_id, year, season)
        ) STRICT;

        CREATE TABLE image_candidates (
            snapshot_id TEXT NOT NULL,
            year        INTEGER NOT NULL,
            season      INTEGER NOT NULL,
            ordinal     INTEGER NOT NULL,
            rank        INTEGER NOT NULL CHECK (rank >= 0),
            variant     TEXT NOT NULL,
            url         TEXT NOT NULL CHECK (url LIKE 'https://%'),
            PRIMARY KEY (snapshot_id, year, season, ordinal, rank),
            FOREIGN KEY (snapshot_id, year, season, ordinal) REFERENCES occurrences (snapshot_id, year, season, ordinal)
        ) STRICT;

        CREATE TABLE assets (
            hash          TEXT PRIMARY KEY CHECK (length(hash) = 64 AND hash NOT GLOB '*[^0-9a-f]*'),
            format        TEXT NOT NULL CHECK (format IN ('jpg', 'png', 'webp')),
            width         INTEGER NOT NULL CHECK (width > 0),
            height        INTEGER NOT NULL CHECK (height > 0),
            byte_length   INTEGER NOT NULL CHECK (byte_length > 0),
            relative_path TEXT NOT NULL UNIQUE CHECK (relative_path = 'assets/' || hash || '.' || format)
        ) STRICT;

        CREATE TABLE collections (
            id                  TEXT PRIMARY KEY CHECK (length(id) = 36),
            name                TEXT NOT NULL CHECK (length(trim(name)) > 0),
            year                INTEGER NOT NULL CHECK (year BETWEEN 1900 AND 2200),
            season              INTEGER CHECK (season BETWEEN 0 AND 3),
            category            INTEGER NOT NULL CHECK (category BETWEEN 0 AND 3),
            applied_snapshot_id TEXT NOT NULL REFERENCES snapshots (id),
            title_language      INTEGER NOT NULL CHECK (title_language BETWEEN 0 AND 3),
            ordinal             INTEGER NOT NULL UNIQUE CHECK (ordinal >= 0)
        ) STRICT;

        CREATE TABLE collection_entries (
            collection_id         TEXT NOT NULL REFERENCES collections (id) ON DELETE CASCADE,
            livechart_id          INTEGER NOT NULL REFERENCES items (livechart_id),
            ordinal               INTEGER NOT NULL CHECK (ordinal >= 0),
            included              INTEGER NOT NULL CHECK (included IN (0, 1)),
            source_missing        INTEGER NOT NULL CHECK (source_missing IN (0, 1)),
            title_override        TEXT CHECK (title_override IS NULL OR length(trim(title_override)) > 0),
            accepted_snapshot_id  TEXT NOT NULL,
            accepted_year         INTEGER NOT NULL,
            accepted_season       INTEGER NOT NULL,
            accepted_ordinal      INTEGER NOT NULL,
            cover_asset_hash      TEXT REFERENCES assets (hash),
            cover_snapshot_id     TEXT,
            cover_year            INTEGER,
            cover_season          INTEGER,
            cover_ordinal         INTEGER,
            cover_requested_url   TEXT,
            cover_final_url       TEXT,
            cover_request_context TEXT,
            PRIMARY KEY (collection_id, livechart_id),
            UNIQUE (collection_id, ordinal),
            FOREIGN KEY (accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal)
                REFERENCES occurrences (snapshot_id, year, season, ordinal),
            FOREIGN KEY (cover_snapshot_id, cover_year, cover_season, cover_ordinal)
                REFERENCES occurrences (snapshot_id, year, season, ordinal),
            CHECK ((cover_asset_hash IS NULL) = (cover_snapshot_id IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_year IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_season IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_ordinal IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_requested_url IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_final_url IS NULL)
               AND (cover_asset_hash IS NULL) = (cover_request_context IS NULL))
        ) STRICT;

        CREATE TABLE boards (
            id            TEXT PRIMARY KEY CHECK (length(id) = 36),
            collection_id TEXT NOT NULL REFERENCES collections (id),
            name          TEXT NOT NULL CHECK (length(trim(name)) > 0),
            ordinal       INTEGER NOT NULL UNIQUE CHECK (ordinal >= 0)
        ) STRICT;

        CREATE TABLE tiers (
            board_id TEXT NOT NULL REFERENCES boards (id) ON DELETE CASCADE,
            id       TEXT NOT NULL CHECK (length(id) = 36),
            ordinal  INTEGER NOT NULL CHECK (ordinal >= 0),
            name     TEXT NOT NULL CHECK (length(trim(name)) > 0),
            color    TEXT NOT NULL CHECK (length(color) = 7 AND color GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'),
            PRIMARY KEY (board_id, id),
            UNIQUE (board_id, ordinal)
        ) STRICT;

        CREATE TABLE board_entries (
            board_id          TEXT NOT NULL REFERENCES boards (id) ON DELETE CASCADE,
            livechart_id      INTEGER NOT NULL REFERENCES items (livechart_id),
            copy_number       INTEGER NOT NULL CHECK (copy_number >= 1),
            seed              INTEGER NOT NULL CHECK (seed >= 0),
            pinned_title      TEXT NOT NULL CHECK (length(trim(pinned_title)) > 0),
            pinned_asset_hash TEXT REFERENCES assets (hash),
            tier_id           TEXT,
            tier_position     INTEGER CHECK (tier_position IS NULL OR tier_position >= 0),
            PRIMARY KEY (board_id, livechart_id, copy_number),
            UNIQUE (board_id, seed),
            UNIQUE (board_id, tier_id, tier_position),
            FOREIGN KEY (board_id, tier_id) REFERENCES tiers (board_id, id),
            CHECK ((tier_id IS NULL) = (tier_position IS NULL))
        ) STRICT;

        CREATE TABLE item_extras (
            livechart_id    INTEGER PRIMARY KEY REFERENCES items (livechart_id) ON DELETE CASCADE,
            retrieved_at    TEXT NOT NULL,
            videos_fetched  INTEGER NOT NULL CHECK (videos_fetched IN (0, 1)),
            profile_fetched INTEGER NOT NULL CHECK (profile_fetched IN (0, 1)),
            rating          TEXT,
            premiere        TEXT,
            metadata        TEXT,
            synopsis        TEXT,
            studios_json    TEXT,
            tags_json       TEXT,
            anilist_url     TEXT CHECK (anilist_url IS NULL OR anilist_url LIKE 'https://%'),
            myanimelist_url TEXT CHECK (myanimelist_url IS NULL OR myanimelist_url LIKE 'https://%')
        ) STRICT;

        CREATE TABLE item_videos (
            livechart_id   INTEGER NOT NULL REFERENCES item_extras (livechart_id) ON DELETE CASCADE,
            ordinal        INTEGER NOT NULL CHECK (ordinal >= 0),
            title          TEXT NOT NULL CHECK (length(trim(title)) > 0),
            url            TEXT NOT NULL CHECK (url LIKE 'https://%'),
            kind           INTEGER NOT NULL CHECK (kind BETWEEN 0 AND 2),
            duration       TEXT,
            audio_language TEXT,
            subtitles      TEXT,
            has_spoilers   INTEGER NOT NULL CHECK (has_spoilers IN (0, 1)),
            PRIMARY KEY (livechart_id, ordinal)
        ) STRICT;
        """;
}
