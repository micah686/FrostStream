-- Fresh-install PostgreSQL baseline: reviewed M001–M098 catalog, seeds and search intent.
-- Runtime-owned Cleipnir tables initialize separately. No existing-install migration is implied.
CREATE SCHEMA auth;



CREATE SCHEMA discovery;



CREATE SCHEMA downloads;



CREATE SCHEMA imports;



CREATE SCHEMA jobs;



CREATE SCHEMA maintenance;



CREATE SCHEMA media;



CREATE SCHEMA metadata;



CREATE SCHEMA notes;



CREATE SCHEMA playlists;



CREATE SCHEMA scheduling;



CREATE SCHEMA statistics;



CREATE SCHEMA storage;



CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;



COMMENT ON EXTENSION pg_trgm IS 'text similarity measurement and index searching based on trigrams';



CREATE TYPE imports.import_session_item_metadata_state AS ENUM (
    'incomplete',
    'ready',
    'edited',
    'placeholder_accepted'
);



CREATE TYPE imports.import_session_item_status AS ENUM (
    'discovered',
    'probed',
    'approved',
    'hashing',
    'uploading',
    'finalizing',
    'imported',
    'already_imported',
    'failed'
);



CREATE TYPE imports.import_session_source_kind AS ENUM (
    'worker_incoming',
    'storage_backend'
);



CREATE TYPE imports.import_session_status AS ENUM (
    'scanning',
    'scan_failed',
    'reviewing',
    'committing',
    'completed',
    'completed_with_failures',
    'cancelled'
);



CREATE TYPE imports.local_import_status AS ENUM (
    'queued',
    'preparing',
    'uploading',
    'completed',
    'already_imported',
    'failed'
);



CREATE TYPE jobs.download_artifact_status AS ENUM (
    'pending',
    'uploading',
    'stored',
    'warning',
    'failed',
    'deleted',
    'residual'
);



CREATE TYPE jobs.download_group_kind AS ENUM (
    'direct',
    'playlist',
    'channel',
    'creator_monitor'
);



CREATE TYPE jobs.download_group_status AS ENUM (
    'queued',
    'expanding',
    'running',
    'stopping',
    'stopped',
    'completed',
    'completed_with_warnings',
    'completed_with_failures',
    'failed'
);



CREATE TYPE jobs.download_job_state AS ENUM (
    'queued',
    'metadata_pending',
    'metadata_resolved',
    'download_pending',
    'downloaded_temp',
    'upload_pending',
    'uploaded',
    'commit_pending',
    'completed',
    'compensating',
    'failed_transient',
    'failed_permanent',
    'dead_lettered',
    'already_downloaded',
    'download_queued',
    'cancelling',
    'cancelled',
    'ignored',
    'provider_halted'
);



CREATE TYPE jobs.download_job_status AS ENUM (
    'queued',
    'running',
    'stopping',
    'stopped',
    'compensating',
    'completed',
    'completed_with_warnings',
    'failed',
    'already_downloaded',
    'ignored'
);



CREATE TYPE jobs.download_stage AS ENUM (
    'none',
    'metadata',
    'duplicate_check',
    'waiting_for_worker',
    'media_acquire',
    'primary_media_upload',
    'meta_sidecar_upload',
    'info_json_upload',
    'thumbnail_upload',
    'caption_upload',
    'rich_metadata_write',
    'finalize',
    'cleanup',
    'compensation',
    'live_chat_upload'
);



CREATE TYPE jobs.download_stage_status AS ENUM (
    'pending',
    'running',
    'retry_waiting',
    'succeeded',
    'skipped',
    'warning',
    'failed',
    'stopped'
);



CREATE TYPE jobs.download_worker_lease_status AS ENUM (
    'active',
    'released',
    'expired',
    'rejected',
    'stopped'
);



CREATE TYPE jobs.failure_kind AS ENUM (
    'unknown',
    'transient',
    'permanent',
    'timeout',
    'cancelled',
    'interrupted',
    'provider_blocked',
    'stopped'
);



CREATE TYPE jobs.playlist_state AS ENUM (
    'pending_metadata',
    'metadata_resolved',
    'failed'
);



CREATE TYPE media.audio_rendition_status AS ENUM (
    'pending',
    'processing',
    'ready',
    'failed'
);



CREATE TYPE media.ingest_origin AS ENUM (
    'download',
    'local_import'
);



CREATE TYPE media.stream_rendition_status AS ENUM (
    'pending',
    'processing',
    'ready',
    'failed'
);



CREATE TYPE metadata.availability_enum AS ENUM (
    'unknown',
    'private',
    'premium_only',
    'subscriber_only',
    'needs_auth',
    'unlisted',
    'public'
);



CREATE TYPE metadata.subtitle_type_enum AS ENUM (
    'subtitles',
    'automatic_captions'
);



CREATE TYPE storage.azure_blob_credential_mode AS ENUM (
    'account_key',
    'connection_string',
    'sas_url'
);



CREATE TYPE storage.google_cloud_storage_credential_mode AS ENUM (
    'credentials_json',
    'credentials_file_path',
    'workload_identity',
    'default_credentials'
);



CREATE TYPE storage.local_storage_protocol AS ENUM (
    'local'
);



CREATE TYPE storage.network_storage_protocol AS ENUM (
    'ftp',
    'ftps',
    'sftp',
    'nfs',
    'smb',
    'cifs'
);



CREATE TYPE storage.s3_compatible_object_storage_provider AS ENUM (
    'aws_s3',
    'min_io',
    'digital_ocean_spaces'
);



CREATE FUNCTION public.fs_index_dependency_metadata_accounts_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_metadata WHERE account_id=OLD.id UNION SELECT media_guid FROM metadata.media_comments WHERE account_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_accounts_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_metadata WHERE account_id=NEW.id UNION SELECT media_guid FROM metadata.media_comments WHERE account_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_accounts_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_metadata WHERE account_id=NEW.id UNION SELECT media_guid FROM metadata.media_comments WHERE account_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_metadata WHERE account_id=OLD.id UNION SELECT media_guid FROM metadata.media_comments WHERE account_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_artists_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_artists a ON a.media_metadata_id=m.id WHERE a.artist_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_artists_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_artists a ON a.media_metadata_id=m.id WHERE a.artist_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_artists_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_artists a ON a.media_metadata_id=m.id WHERE a.artist_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_artists a ON a.media_metadata_id=m.id WHERE a.artist_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_audio_stream_details_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=OLD.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_audio_stream_details_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=NEW.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_audio_stream_details_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=NEW.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=OLD.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_categories_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_categories a ON a.media_metadata_id=m.id WHERE a.category_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_categories_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_categories a ON a.media_metadata_id=m.id WHERE a.category_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_categories_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_categories a ON a.media_metadata_id=m.id WHERE a.category_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_categories a ON a.media_metadata_id=m.id WHERE a.category_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_genres_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_genres a ON a.media_metadata_id=m.id WHERE a.genre_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_genres_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_genres a ON a.media_metadata_id=m.id WHERE a.genre_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_genres_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_genres a ON a.media_metadata_id=m.id WHERE a.genre_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_genres a ON a.media_metadata_id=m.id WHERE a.genre_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_artists_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_artists_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_artists_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_categories_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_categories_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_categories_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_genres_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_genres_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_genres_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_streams_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_base WHERE id=OLD.media_base_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_streams_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_base WHERE id=NEW.media_base_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_streams_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_base WHERE id=NEW.media_base_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT media_guid FROM metadata.media_base WHERE id=OLD.media_base_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_tags_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_tags_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_media_tags_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=NEW.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m WHERE m.id=OLD.media_metadata_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_tags_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_tags a ON a.media_metadata_id=m.id WHERE a.tag_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_tags_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_tags a ON a.media_metadata_id=m.id WHERE a.tag_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_tags_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_tags a ON a.media_metadata_id=m.id WHERE a.tag_id=NEW.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT m.media_guid FROM metadata.media_metadata m JOIN metadata.media_tags a ON a.media_metadata_id=m.id WHERE a.tag_id=OLD.id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_video_stream_details_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=OLD.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_video_stream_details_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=NEW.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_dependency_metadata_video_stream_details_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=NEW.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT replace(media_guid::text,'-',''),1 FROM (SELECT b.media_guid FROM metadata.media_base b JOIN metadata.media_streams s ON s.media_base_id=b.id WHERE s.id=OLD.media_stream_id) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_media_delete() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) VALUES(replace(OLD.media_guid::text,'-',''),1) ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN OLD; END; $$;



CREATE FUNCTION public.fs_index_media_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) VALUES(replace(NEW.media_guid::text,'-',''),1) ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;



CREATE FUNCTION public.fs_index_media_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN INSERT INTO search_pending_index(media_guid,revision) VALUES(replace(NEW.media_guid::text,'-',''),1) ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;INSERT INTO search_pending_index(media_guid,revision) VALUES(replace(OLD.media_guid::text,'-',''),1) ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL; RETURN NEW; END; $$;





CREATE TABLE auth.access_policies (
    policy_id uuid NOT NULL,
    name character varying(200) NOT NULL,
    description character varying(2000),
    enabled boolean DEFAULT true NOT NULL,
    sync_status character varying(32) DEFAULT 'pending'::character varying NOT NULL,
    sync_error character varying(2000),
    version bigint DEFAULT 1 NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    created_by_subject character varying(255),
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_by_subject character varying(255)
);



CREATE TABLE auth.access_policy_age_tiers (
    policy_id uuid NOT NULL,
    minimum_age integer NOT NULL,
    CONSTRAINT ck_access_policy_age_tiers_nonnegative CHECK ((minimum_age >= 0))
);



COMMENT ON TABLE auth.access_policy_age_tiers IS 'Inclusive deny thresholds. Assigned principals are denied when age_limit >= minimum_age.';



CREATE TABLE auth.access_policy_assignments (
    policy_id uuid NOT NULL,
    principal_type character varying(16) NOT NULL,
    principal_id character varying(255) NOT NULL,
    CONSTRAINT ck_access_policy_assignments_type CHECK (((principal_type)::text = ANY ((ARRAY['user'::character varying, 'group'::character varying])::text[])))
);



COMMENT ON TABLE auth.access_policy_assignments IS 'User and group principals to which endpoint grants and media denies apply.';



CREATE TABLE auth.access_policy_bundles (
    policy_id uuid NOT NULL,
    bundle_id character varying(255) NOT NULL
);



COMMENT ON TABLE auth.access_policy_bundles IS 'Endpoint bundles positively granted to principals assigned to the policy.';



CREATE TABLE auth.access_policy_media (
    policy_id uuid NOT NULL,
    media_guid uuid NOT NULL
);



COMMENT ON TABLE auth.access_policy_media IS 'Media GUID deny scopes. Assigned principals cannot watch matching media.';



CREATE TABLE auth.access_policy_providers (
    policy_id uuid NOT NULL,
    provider character varying(255) NOT NULL,
    CONSTRAINT ck_access_policy_providers_normalized CHECK ((((provider)::text <> ''::text) AND ((provider)::text = lower(btrim((provider)::text)))))
);



COMMENT ON TABLE auth.access_policy_providers IS 'Normalized provider deny scopes. Assigned principals cannot watch matching providers.';



CREATE TABLE auth.cookie_profiles (
    id uuid NOT NULL,
    owner_subject character varying(255) NOT NULL,
    profile_key character varying(100) NOT NULL,
    site character varying(255),
    display_name character varying(255),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_updated timestamp with time zone
);



CREATE TABLE auth.froststream_users (
    id uuid NOT NULL,
    authentik_subject_id character varying(255) NOT NULL,
    display_name character varying(255) NOT NULL,
    last_seen_at timestamp with time zone,
    preferences jsonb,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_updated timestamp with time zone
);



CREATE TABLE discovery.creator_sources (
    id bigint NOT NULL,
    platform character varying(50) NOT NULL,
    source_type character varying(50) NOT NULL,
    source_url character varying(4096) NOT NULL,
    scan_enabled boolean DEFAULT true NOT NULL,
    incremental_page_size integer DEFAULT 50 NOT NULL,
    consecutive_known_threshold integer DEFAULT 25 NOT NULL,
    full_rescan_interval_days integer DEFAULT 30 NOT NULL,
    metadata_refresh_window integer DEFAULT 25 NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_updated timestamp with time zone,
    update_check_interval_hours integer DEFAULT 6 NOT NULL,
    account_id bigint,
    config_set_owner_subject character varying(255),
    config_set_key character varying(100),
    CONSTRAINT ck_creator_sources_scan_settings_positive CHECK (((incremental_page_size > 0) AND (consecutive_known_threshold > 0) AND (full_rescan_interval_days > 0) AND (metadata_refresh_window > 0)))
);



ALTER TABLE discovery.creator_sources ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME discovery.creator_sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE discovery.discovered_media (
    id bigint NOT NULL,
    creator_source_id bigint NOT NULL,
    platform character varying(50) NOT NULL,
    extractor character varying(255) NOT NULL,
    external_media_id character varying(512) NOT NULL,
    canonical_url character varying(4096) NOT NULL,
    title character varying(2048),
    duration_seconds double precision,
    thumbnail_url character varying(4096),
    live_status character varying(100),
    availability character varying(100),
    discovery_status character varying(50) DEFAULT 'Discovered'::character varying NOT NULL,
    metadata_status character varying(50) DEFAULT 'PendingEnrichment'::character varying NOT NULL,
    first_seen_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_seen_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    missed_full_scan_count integer DEFAULT 0 NOT NULL,
    last_changed_at timestamp with time zone,
    last_enqueued_at timestamp with time zone,
    last_updated timestamp with time zone,
    ignored_keyword character varying(200)
);



ALTER TABLE discovery.discovered_media ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME discovery.discovered_media_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE downloads.download_config_sets (
    id bigint NOT NULL,
    owner_subject character varying(255) NOT NULL,
    key character varying(100) NOT NULL,
    name character varying(255) NOT NULL,
    description character varying(2000),
    storage_key character varying(100),
    cookie_profile_key character varying(100),
    ytdlp_options_json jsonb,
    priority integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    ignore_keywords_json jsonb,
    worker_tag character varying(50),
    CONSTRAINT ck_download_config_sets_key_format CHECK (((key)::text ~ '^[a-z0-9-]{2,100}$'::text)),
    CONSTRAINT ck_download_config_sets_worker_tag_format CHECK (((worker_tag IS NULL) OR ((worker_tag)::text ~ '^[a-z0-9-]{2,50}$'::text)))
);



ALTER TABLE downloads.download_config_sets ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME downloads.download_config_sets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE downloads.download_option_presets (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    name character varying(255) NOT NULL,
    description character varying(2000),
    ytdlp_options_json jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_updated timestamp with time zone,
    CONSTRAINT ck_download_option_presets_key_format CHECK (((key)::text ~ '^[a-z0-9-]{2,100}$'::text))
);



ALTER TABLE downloads.download_option_presets ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME downloads.download_option_presets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE imports.import_session_items (
    item_id uuid NOT NULL,
    session_id uuid NOT NULL,
    relative_path character varying(2048) NOT NULL,
    file_name character varying(1024) NOT NULL,
    file_size_bytes bigint NOT NULL,
    file_mtime timestamp with time zone,
    sidecars jsonb,
    provider character varying(255),
    source_media_id character varying(512),
    source_url character varying(4096),
    title character varying(1024),
    probe_metadata jsonb,
    scan_metadata jsonb,
    enriched_metadata jsonb,
    user_metadata jsonb,
    metadata_state imports.import_session_item_metadata_state NOT NULL,
    excluded boolean DEFAULT false NOT NULL,
    status imports.import_session_item_status NOT NULL,
    attempt integer DEFAULT 0 NOT NULL,
    content_hash_xxh128 character varying(64),
    media_guid uuid,
    storage_path character varying(2048),
    storage_version character varying(255),
    meta_storage_path character varying(2048),
    info_json_storage_path character varying(2048),
    thumbnail_storage_path character varying(2048),
    caption_storage_paths jsonb,
    error_code character varying(255),
    error_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone,
    metadata_source character varying(32) DEFAULT 'Placeholder'::character varying NOT NULL,
    metadata_fetch_state character varying(32) DEFAULT 'NotAttempted'::character varying NOT NULL,
    metadata_fetch_attempt integer DEFAULT 0 NOT NULL,
    metadata_fetch_message character varying(4096)
);



CREATE TABLE imports.import_session_mappings (
    mapping_id uuid NOT NULL,
    session_id uuid NOT NULL,
    object_bucket character varying(255) NOT NULL,
    object_key character varying(1024) NOT NULL,
    format character varying(32) NOT NULL,
    matched_count integer DEFAULT 0 NOT NULL,
    unmatched_count integer DEFAULT 0 NOT NULL,
    applied_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



CREATE TABLE imports.import_sessions (
    session_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    status imports.import_session_status NOT NULL,
    source_kind imports.import_session_source_kind NOT NULL,
    source_root character varying(2048) NOT NULL,
    sub_path character varying(2048),
    storage_key character varying(100) NOT NULL,
    worker_tag character varying(128),
    requested_by character varying(255),
    total_items integer DEFAULT 0 NOT NULL,
    probed_items integer DEFAULT 0 NOT NULL,
    ready_items integer DEFAULT 0 NOT NULL,
    incomplete_items integer DEFAULT 0 NOT NULL,
    excluded_items integer DEFAULT 0 NOT NULL,
    approved_items integer DEFAULT 0 NOT NULL,
    imported_items integer DEFAULT 0 NOT NULL,
    already_imported_items integer DEFAULT 0 NOT NULL,
    failed_items integer DEFAULT 0 NOT NULL,
    max_parallel_items integer DEFAULT 6 NOT NULL,
    error_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone,
    delete_source_files boolean DEFAULT false NOT NULL
);



CREATE TABLE imports.local_import_batches (
    batch_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    status imports.local_import_status NOT NULL,
    manifest_object_bucket character varying(255) NOT NULL,
    manifest_object_key character varying(1024) NOT NULL,
    source_root character varying(2048) NOT NULL,
    storage_key character varying(100) NOT NULL,
    requested_by character varying(255),
    requested_by_context character varying(255),
    total_items integer DEFAULT 0 NOT NULL,
    completed_items integer DEFAULT 0 NOT NULL,
    already_imported_items integer DEFAULT 0 NOT NULL,
    failed_items integer DEFAULT 0 NOT NULL,
    error_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone
);



CREATE TABLE imports.local_import_items (
    item_id uuid NOT NULL,
    batch_id uuid NOT NULL,
    item_index integer NOT NULL,
    status imports.local_import_status NOT NULL,
    source_root character varying(2048) NOT NULL,
    relative_path character varying(2048) NOT NULL,
    storage_key character varying(100) NOT NULL,
    provider character varying(255),
    source_media_id character varying(512),
    source_last_modified timestamp with time zone,
    source_url character varying(4096),
    title character varying(1024),
    file_size_bytes bigint,
    content_hash_xxh128 character varying(64),
    media_guid uuid,
    storage_path character varying(2048),
    storage_version character varying(255),
    meta_storage_path character varying(2048),
    info_json_storage_path character varying(2048),
    thumbnail_storage_path character varying(2048),
    caption_storage_paths jsonb,
    error_code character varying(255),
    error_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone
);



CREATE TABLE jobs.creator_scan_state (
    creator_source_id bigint NOT NULL,
    last_successful_scan_at timestamp with time zone,
    last_full_scan_at timestamp with time zone,
    last_seen_high_watermark character varying(512),
    next_full_scan_start_index integer,
    avatar_url character varying(4096),
    avatar_content_hash character varying(64),
    banner_url character varying(4096),
    banner_content_hash character varying(64),
    assets_last_refreshed_at timestamp with time zone,
    assets_last_attempt_at timestamp with time zone,
    assets_attempt_count integer DEFAULT 0 NOT NULL,
    assets_last_error character varying(2048),
    CONSTRAINT ck_creator_scan_state_next_full_scan_start_index_positive CHECK (((next_full_scan_start_index IS NULL) OR (next_full_scan_start_index > 0)))
);



CREATE TABLE jobs.download_artifacts (
    id bigint NOT NULL,
    run_id uuid NOT NULL,
    job_id uuid NOT NULL,
    stage jobs.download_stage NOT NULL,
    artifact_key character varying(512) NOT NULL,
    kind integer NOT NULL,
    required boolean NOT NULL,
    status jobs.download_artifact_status NOT NULL,
    temp_file_ref character varying(2048),
    storage_key character varying(100),
    storage_path character varying(2048),
    storage_version character varying(255),
    content_hash_xxh128 character varying(64),
    size_bytes bigint,
    warning_code character varying(255),
    warning_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



ALTER TABLE jobs.download_artifacts ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.download_artifacts_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.download_groups (
    group_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    kind jobs.download_group_kind NOT NULL,
    status jobs.download_group_status NOT NULL,
    source_url character varying(4096) NOT NULL,
    requested_by character varying(255),
    storage_key character varying(100),
    total_jobs integer DEFAULT 0 NOT NULL,
    completed_jobs integer DEFAULT 0 NOT NULL,
    warning_jobs integer DEFAULT 0 NOT NULL,
    failed_jobs integer DEFAULT 0 NOT NULL,
    failure_code character varying(255),
    failure_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone
);



CREATE TABLE jobs.download_job_history (
    id bigint NOT NULL,
    job_id uuid NOT NULL,
    message_id uuid NOT NULL,
    operation_key character varying(512) NOT NULL,
    event_name character varying(255) NOT NULL,
    payload_json jsonb,
    recorded_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



ALTER TABLE jobs.download_job_history ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.download_job_history_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.download_job_progress_log (
    id bigint NOT NULL,
    job_id uuid NOT NULL,
    sequence integer NOT NULL,
    message character varying(2048) NOT NULL,
    recorded_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



ALTER TABLE jobs.download_job_progress_log ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.download_job_progress_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.download_job_runs (
    run_id uuid NOT NULL,
    job_id uuid NOT NULL,
    run_number integer NOT NULL,
    status jobs.download_job_status NOT NULL,
    stage jobs.download_stage NOT NULL,
    stage_status jobs.download_stage_status NOT NULL,
    failure_kind jobs.failure_kind,
    failure_code character varying(255),
    failure_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    started_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    ended_at timestamp with time zone
);



CREATE TABLE jobs.download_job_warnings (
    id bigint NOT NULL,
    run_id uuid NOT NULL,
    job_id uuid NOT NULL,
    stage jobs.download_stage NOT NULL,
    artifact_key character varying(512) DEFAULT ''::character varying NOT NULL,
    warning_code character varying(255) NOT NULL,
    warning_message character varying(4096) NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



ALTER TABLE jobs.download_job_warnings ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.download_job_warnings_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.download_jobs (
    job_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    state jobs.download_job_state NOT NULL,
    source_url character varying(4096) NOT NULL,
    requested_by character varying(255),
    storage_key character varying(100),
    attempt_metadata integer DEFAULT 0 NOT NULL,
    attempt_download integer DEFAULT 0 NOT NULL,
    attempt_upload integer DEFAULT 0 NOT NULL,
    temp_file_ref character varying(2048),
    file_size_bytes bigint,
    content_hash_xxh128 character varying(64),
    storage_version character varying(255),
    failure_kind jobs.failure_kind,
    failure_code character varying(255),
    failure_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone,
    info_json_storage_path character varying(2048),
    info_json_content_hash_xxh128 character varying(64),
    info_json_size_bytes bigint,
    meta_storage_path character varying(2048),
    priority integer DEFAULT 0 NOT NULL,
    ingest_origin media.ingest_origin DEFAULT 'download'::media.ingest_origin NOT NULL,
    ignored_keyword character varying(200),
    source_kind integer DEFAULT 0 NOT NULL,
    status jobs.download_job_status DEFAULT 'queued'::jobs.download_job_status NOT NULL,
    stage jobs.download_stage DEFAULT 'none'::jobs.download_stage NOT NULL,
    stage_status jobs.download_stage_status DEFAULT 'pending'::jobs.download_stage_status NOT NULL,
    current_run_id uuid,
    current_run_number integer DEFAULT 0 NOT NULL,
    current_attempt integer DEFAULT 0 NOT NULL,
    current_artifact_key character varying(512),
    warning_count integer DEFAULT 0 NOT NULL,
    stop_requested_at timestamp with time zone,
    stop_requested_by character varying(255),
    stop_reason character varying(512)
);



CREATE TABLE jobs.download_provider_circuits (
    provider character varying(255) NOT NULL,
    is_open boolean NOT NULL,
    reason character varying(4096),
    opened_at timestamp with time zone,
    cleared_at timestamp with time zone
);



CREATE TABLE jobs.download_stage_attempts (
    id bigint NOT NULL,
    run_id uuid NOT NULL,
    job_id uuid NOT NULL,
    stage jobs.download_stage NOT NULL,
    artifact_key character varying(512) DEFAULT ''::character varying NOT NULL,
    attempt integer NOT NULL,
    status jobs.download_stage_status NOT NULL,
    dispatch_id uuid NOT NULL,
    operation_key character varying(512) NOT NULL,
    failure_kind jobs.failure_kind,
    failure_code character varying(255),
    failure_message character varying(4096),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    started_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    ended_at timestamp with time zone
);



ALTER TABLE jobs.download_stage_attempts ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.download_stage_attempts_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.download_startup_state (
    name text NOT NULL,
    generation_started_at timestamp with time zone NOT NULL
);



CREATE TABLE jobs.download_worker_leases (
    dispatch_id uuid NOT NULL,
    run_id uuid NOT NULL,
    job_id uuid NOT NULL,
    stage jobs.download_stage NOT NULL,
    artifact_key character varying(512) DEFAULT ''::character varying NOT NULL,
    attempt integer NOT NULL,
    worker_instance_id character varying(255) NOT NULL,
    status jobs.download_worker_lease_status NOT NULL,
    acquired_at timestamp with time zone NOT NULL,
    last_heartbeat_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    released_at timestamp with time zone
);



CREATE TABLE jobs.failed_download_jobs (
    job_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    failed_state jobs.download_job_state NOT NULL,
    failure_kind jobs.failure_kind NOT NULL,
    failure_code character varying(255),
    failure_message character varying(4096) NOT NULL,
    last_payload_json jsonb,
    failed_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



CREATE TABLE jobs.playlist_items (
    id bigint NOT NULL,
    playlist_id uuid NOT NULL,
    job_id uuid NOT NULL,
    playlist_index integer NOT NULL,
    entry_url character varying(4096) NOT NULL,
    entry_title character varying(2048)
);



ALTER TABLE jobs.playlist_items ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.playlist_items_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.playlist_scan_entries (
    id bigint NOT NULL,
    playlist_id uuid NOT NULL,
    playlist_index integer NOT NULL,
    entry_url character varying(4096) NOT NULL,
    entry_title character varying(2048)
);



ALTER TABLE jobs.playlist_scan_entries ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME jobs.playlist_scan_entries_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE jobs.playlist_source_metadata (
    playlist_id uuid NOT NULL,
    provider_playlist_id character varying(512),
    title character varying(2048),
    total_items integer DEFAULT 0 NOT NULL,
    last_scanned_at timestamp with time zone
);



CREATE TABLE jobs.playlists (
    playlist_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    state jobs.playlist_state NOT NULL,
    source_url character varying(4096) NOT NULL,
    requested_by character varying(255),
    storage_key character varying(100),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    completed_at timestamp with time zone,
    encode_for_playlist boolean DEFAULT false NOT NULL,
    config_set_key character varying(100),
    cookie_secret_path character varying(512),
    ytdlp_options_json jsonb,
    priority integer DEFAULT 0 NOT NULL,
    fetch_comments boolean DEFAULT false NOT NULL,
    worker_tag character varying(50),
    CONSTRAINT ck_playlists_worker_tag_format CHECK (((worker_tag IS NULL) OR ((worker_tag)::text ~ '^[a-z0-9-]{2,50}$'::text)))
);



CREATE TABLE jobs.processed_messages (
    message_id uuid NOT NULL,
    operation_key character varying(512) NOT NULL,
    job_id uuid NOT NULL,
    processed_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



CREATE TABLE media.audio_encoding_status (
    media_guid uuid NOT NULL,
    account_id bigint NOT NULL,
    is_encoded boolean DEFAULT false NOT NULL,
    storage_key character varying(100),
    storage_path character varying(2048),
    encoded_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE media.audio_renditions (
    rendition_id uuid NOT NULL,
    media_guid uuid NOT NULL,
    source_version_num integer NOT NULL,
    status media.audio_rendition_status NOT NULL,
    storage_key character varying(100) NOT NULL,
    storage_path character varying(2048),
    content_hash_xxh128 character varying(64),
    size_bytes bigint,
    duration_seconds integer,
    error_message character varying(4096),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE media.media (
    media_guid uuid NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



CREATE TABLE media.media_content_id_versions (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    content_hash_xxh128 character varying(64) NOT NULL,
    storage_key character varying(100) NOT NULL,
    storage_path character varying(2048) NOT NULL,
    version_num integer NOT NULL,
    ingest_origin media.ingest_origin DEFAULT 'download'::media.ingest_origin NOT NULL
);



ALTER TABLE media.media_content_id_versions ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME media.media_content_id_versions_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE media.media_source_versions (
    id bigint NOT NULL,
    provider character varying(255),
    source_media_id character varying(512),
    source_last_modified timestamp with time zone,
    media_guid uuid NOT NULL,
    latest_job_id uuid
);



ALTER TABLE media.media_source_versions ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME media.media_source_versions_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE media.stream_renditions (
    rendition_id uuid NOT NULL,
    media_guid uuid NOT NULL,
    source_version_num integer NOT NULL,
    status media.stream_rendition_status NOT NULL,
    storage_key character varying(100) NOT NULL,
    storage_path character varying(2048),
    size_bytes bigint,
    duration_seconds integer,
    error_message character varying(4096),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE media.user_media_likes (
    owner_subject text NOT NULL,
    media_guid uuid NOT NULL,
    liked_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);



CREATE TABLE media.watch_states (
    owner_subject text NOT NULL,
    media_guid uuid NOT NULL,
    position_seconds double precision,
    duration_seconds double precision,
    completed boolean DEFAULT false NOT NULL,
    watched_at timestamp with time zone,
    last_played_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_watch_states_duration_positive CHECK (((duration_seconds IS NULL) OR (duration_seconds > (0)::double precision))),
    CONSTRAINT ck_watch_states_position_nonnegative CHECK (((position_seconds IS NULL) OR (position_seconds >= (0)::double precision)))
);



CREATE TABLE metadata.account_external_ids (
    id bigint NOT NULL,
    account_id bigint NOT NULL,
    platform character varying(50) NOT NULL,
    id_kind character varying(50) NOT NULL,
    external_id text NOT NULL
);



ALTER TABLE metadata.account_external_ids ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.account_external_ids_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.accounts (
    id bigint NOT NULL,
    platform character varying(50) NOT NULL,
    account_name text NOT NULL,
    account_handle text NOT NULL,
    account_url text,
    account_creation_date timestamp without time zone,
    account_follower_count bigint,
    is_verified boolean DEFAULT false NOT NULL,
    account_description text,
    avatar_storage_path text,
    banner_storage_path text,
    storage_key character varying(255)
);



ALTER TABLE metadata.accounts ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.accounts_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.artists (
    id bigint NOT NULL,
    artist_name text NOT NULL
);



ALTER TABLE metadata.artists ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.artists_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.audio_stream_details (
    id bigint NOT NULL,
    media_stream_id bigint NOT NULL,
    channels integer DEFAULT 0 NOT NULL,
    channel_layout text NOT NULL,
    sample_rate_hz integer DEFAULT 0 NOT NULL,
    profile text NOT NULL
);



ALTER TABLE metadata.audio_stream_details ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.audio_stream_details_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.cast_members (
    id bigint NOT NULL,
    cast_name text NOT NULL
);



ALTER TABLE metadata.cast_members ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.cast_members_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.categories (
    id bigint NOT NULL,
    category_name text NOT NULL
);



ALTER TABLE metadata.categories ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.categories_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.chapter_data (
    id bigint NOT NULL,
    media_base_id bigint NOT NULL,
    title text NOT NULL,
    start_ticks bigint DEFAULT 0 NOT NULL,
    end_ticks bigint DEFAULT 0 NOT NULL
);



ALTER TABLE metadata.chapter_data ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.chapter_data_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.genres (
    id bigint NOT NULL,
    genre_name text NOT NULL
);



ALTER TABLE metadata.genres ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.genres_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_artists (
    media_metadata_id bigint NOT NULL,
    artist_id bigint NOT NULL,
    is_album_artist boolean DEFAULT false NOT NULL
);



CREATE TABLE metadata.media_base (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    duration_ticks bigint DEFAULT 0 NOT NULL
);



ALTER TABLE metadata.media_base ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.media_base_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_captions (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    storage_path text NOT NULL,
    caption_type metadata.subtitle_type_enum NOT NULL,
    two_digit_language_code character varying(10) NOT NULL,
    name text,
    storage_key character varying(255)
);



ALTER TABLE metadata.media_captions ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.media_captions_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_cast (
    media_metadata_id bigint NOT NULL,
    cast_member_id bigint NOT NULL
);



CREATE TABLE metadata.media_categories (
    media_metadata_id bigint NOT NULL,
    category_id bigint NOT NULL
);



CREATE TABLE metadata.media_comments (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    comment_id text NOT NULL,
    parent_comment_id text,
    text_comment text NOT NULL,
    account_id bigint NOT NULL,
    comment_timestamp timestamp without time zone NOT NULL,
    like_count integer,
    dislike_count integer,
    is_favorited boolean DEFAULT false NOT NULL,
    is_uploader boolean DEFAULT false NOT NULL,
    is_pinned boolean DEFAULT false NOT NULL
);



ALTER TABLE metadata.media_comments ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.media_comments_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_format_details (
    id bigint NOT NULL,
    media_base_id bigint NOT NULL,
    duration_ticks bigint DEFAULT 0 NOT NULL,
    start_time_ticks bigint DEFAULT 0 NOT NULL,
    format_long_names text NOT NULL,
    stream_count integer DEFAULT 0 NOT NULL,
    bit_rate double precision DEFAULT 0 NOT NULL
);



ALTER TABLE metadata.media_format_details ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.media_format_details_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_genres (
    media_metadata_id bigint NOT NULL,
    genre_id bigint NOT NULL
);



CREATE TABLE metadata.media_live_chat (
    media_guid uuid NOT NULL,
    version_num integer,
    message_count bigint DEFAULT 0 NOT NULL,
    first_offset_ms bigint DEFAULT 0 NOT NULL,
    last_offset_ms bigint DEFAULT 0 NOT NULL,
    ingested_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



CREATE TABLE metadata.media_metadata (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    account_id bigint NOT NULL,
    external_media_id text,
    metadata_scrape_date timestamp without time zone NOT NULL,
    thumbnail_storage_path text,
    age_limit integer,
    average_rating double precision,
    like_count bigint,
    dislike_count bigint,
    duration double precision,
    description text,
    release_date timestamp without time zone,
    title text,
    was_live boolean DEFAULT false NOT NULL,
    webpage_url text,
    view_count bigint,
    comment_count bigint,
    availability metadata.availability_enum,
    location text,
    storage_key character varying(255)
);



ALTER TABLE metadata.media_metadata ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.media_metadata_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_streams (
    id bigint NOT NULL,
    media_base_id bigint NOT NULL,
    stream_type character varying(20) NOT NULL,
    is_primary boolean DEFAULT false NOT NULL,
    codec_name text NOT NULL,
    codec_long_name text NOT NULL,
    bit_rate bigint DEFAULT 0 NOT NULL,
    bit_depth integer,
    start_time_ticks bigint DEFAULT 0 NOT NULL,
    duration_ticks bigint DEFAULT 0 NOT NULL,
    language character varying(50)
);



ALTER TABLE metadata.media_streams ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.media_streams_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.media_tags (
    media_metadata_id bigint NOT NULL,
    tag_id bigint NOT NULL
);



CREATE TABLE metadata.music_metadata (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    album_title text NOT NULL,
    album_type text,
    disc_number integer,
    release_year integer,
    track_title text NOT NULL,
    track_number integer NOT NULL,
    composer text
);



ALTER TABLE metadata.music_metadata ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.music_metadata_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.series_metadata (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    series_name text NOT NULL,
    season_count integer,
    season_number integer NOT NULL,
    season_name text,
    episode_number integer NOT NULL,
    episode_name text NOT NULL
);



ALTER TABLE metadata.series_metadata ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.series_metadata_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.tags (
    id bigint NOT NULL,
    tag_name text NOT NULL
);



ALTER TABLE metadata.tags ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.tags_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE metadata.video_stream_details (
    id bigint NOT NULL,
    media_stream_id bigint NOT NULL,
    avg_frame_rate double precision DEFAULT 0 NOT NULL,
    bits_per_raw_sample integer DEFAULT 0 NOT NULL,
    display_aspect_ratio_width integer DEFAULT 0 NOT NULL,
    display_aspect_ratio_height integer DEFAULT 0 NOT NULL,
    profile text NOT NULL,
    width integer DEFAULT 0 NOT NULL,
    height integer DEFAULT 0 NOT NULL,
    pixel_format text NOT NULL,
    rotation integer DEFAULT 0 NOT NULL,
    color_space text NOT NULL,
    color_transfer text NOT NULL,
    color_primaries text NOT NULL,
    hdr_type character varying(20) DEFAULT 'SDR'::character varying NOT NULL
);



ALTER TABLE metadata.video_stream_details ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME metadata.video_stream_details_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE notes.user_notes (
    id bigint NOT NULL,
    owner_subject character varying(255) NOT NULL,
    target_type character varying(32) NOT NULL,
    target_id character varying(64) NOT NULL,
    note character varying(8192) NOT NULL,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



ALTER TABLE notes.user_notes ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME notes.user_notes_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE playlists.media_playlist_membership (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    playlist_id uuid NOT NULL,
    playlist_index integer NOT NULL
);



ALTER TABLE playlists.media_playlist_membership ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME playlists.media_playlist_membership_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE playlists.playlist_metadata (
    id bigint NOT NULL,
    playlist_id uuid NOT NULL,
    title text
);



ALTER TABLE playlists.playlist_metadata ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME playlists.playlist_metadata_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE playlists.user_playlist_items (
    id bigint NOT NULL,
    playlist_id uuid NOT NULL,
    media_guid uuid NOT NULL,
    "position" integer NOT NULL,
    added_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



ALTER TABLE playlists.user_playlist_items ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME playlists.user_playlist_items_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE playlists.user_playlists (
    playlist_id uuid NOT NULL,
    owner_subject character varying(255) NOT NULL,
    name character varying(255) NOT NULL,
    description character varying(2048),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    updated_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL
);



CREATE TABLE public."VersionInfo" (
    "Version" bigint NOT NULL,
    "AppliedOn" timestamp without time zone,
    "Description" character varying(1024)
);



CREATE TABLE public.search_pending_index (
    media_guid text NOT NULL,
    revision bigint DEFAULT 1 NOT NULL,
    applied bigint DEFAULT 0 NOT NULL,
    available bigint DEFAULT 0 NOT NULL,
    lease_until bigint DEFAULT 0 NOT NULL,
    token text,
    attempts integer DEFAULT 0 NOT NULL,
    last_error text
);



CREATE TABLE scheduling.scheduled_tasks (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    task_type character varying(100) NOT NULL,
    cron character varying(255),
    interval_seconds integer,
    timezone character varying(100) DEFAULT 'UTC'::character varying NOT NULL,
    enabled boolean DEFAULT false NOT NULL,
    catchup_policy character varying(32) DEFAULT 'Coalesce'::character varying NOT NULL,
    last_attempt_at timestamp with time zone,
    last_success_at timestamp with time zone,
    next_due_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_updated timestamp with time zone,
    last_run_status character varying(32),
    retention_days integer DEFAULT 0 NOT NULL,
    include_failed boolean DEFAULT false NOT NULL,
    CONSTRAINT ck_scheduled_tasks_interval_positive CHECK (((interval_seconds IS NULL) OR (interval_seconds > 0))),
    CONSTRAINT ck_scheduled_tasks_key_format CHECK (((key)::text ~ '^[a-z0-9-]{2,100}$'::text)),
    CONSTRAINT ck_scheduled_tasks_retention_days_nonnegative CHECK ((retention_days >= 0)),
    CONSTRAINT ck_scheduled_tasks_trigger_xor CHECK ((((cron IS NOT NULL) AND (interval_seconds IS NULL)) OR ((cron IS NULL) AND (interval_seconds IS NOT NULL))))
);



ALTER TABLE scheduling.scheduled_tasks ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME scheduling.scheduled_tasks_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE statistics.account_daily_states (
    day date NOT NULL,
    account_id bigint NOT NULL,
    state character varying(32) NOT NULL,
    job_count bigint DEFAULT 0 NOT NULL
);



CREATE TABLE statistics.channel_media_downloads (
    id bigint NOT NULL,
    media_guid uuid NOT NULL,
    account_id bigint NOT NULL,
    platform character varying(50) NOT NULL,
    bytes bigint DEFAULT 0 NOT NULL,
    duration_seconds double precision DEFAULT 0 NOT NULL,
    completed_at timestamp with time zone NOT NULL
);



ALTER TABLE statistics.channel_media_downloads ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME statistics.channel_media_downloads_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE statistics.creator_source_daily_states (
    day date NOT NULL,
    creator_source_id bigint NOT NULL,
    state character varying(32) NOT NULL,
    job_count bigint DEFAULT 0 NOT NULL
);



CREATE TABLE statistics.download_daily_activity (
    day date NOT NULL,
    state character varying(32) NOT NULL,
    job_count bigint DEFAULT 0 NOT NULL,
    bytes bigint DEFAULT 0 NOT NULL,
    duration_seconds double precision DEFAULT 0 NOT NULL
);



CREATE TABLE storage.storage_keys (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    method character varying(50) NOT NULL,
    description character varying(500),
    created_at timestamp with time zone DEFAULT (now() AT TIME ZONE 'UTC'::text) NOT NULL,
    last_updated timestamp with time zone,
    worker_tag character varying(50),
    CONSTRAINT ck_storage_keys_key_format CHECK (((key)::text ~ '^[a-z0-9-]{2,100}$'::text))
);



ALTER TABLE storage.storage_keys ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME storage.storage_keys_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE storage.storage_keys_local (
    storage_key_id integer NOT NULL,
    protocol storage.local_storage_protocol NOT NULL,
    path character varying(2048) NOT NULL
);



CREATE TABLE storage.storage_keys_network (
    storage_key_id integer NOT NULL,
    protocol storage.network_storage_protocol NOT NULL,
    host character varying(255) NOT NULL,
    port integer,
    username character varying(255),
    base_path character varying(2048),
    mount_path character varying(2048),
    share_name character varying(255),
    domain character varying(255),
    export_path character varying(2048),
    nfs_user_id integer,
    nfs_group_id integer
);



CREATE TABLE storage.storage_keys_object_azure_blob (
    storage_key_id integer NOT NULL,
    credential_mode storage.azure_blob_credential_mode NOT NULL,
    container_name character varying(255),
    azure_account_name character varying(255)
);



CREATE TABLE storage.storage_keys_object_google_cloud_storage (
    storage_key_id integer CONSTRAINT storage_keys_object_google_cloud_storag_storage_key_id_not_null NOT NULL,
    bucket_name character varying(255) NOT NULL,
    credential_mode storage.google_cloud_storage_credential_mode CONSTRAINT storage_keys_object_google_cloud_stora_credential_mode_not_null NOT NULL,
    gcp_credentials_file_path character varying(2048),
    gcp_project_id character varying(255)
);



CREATE TABLE storage.storage_keys_object_s3_compatible (
    storage_key_id integer NOT NULL,
    provider storage.s3_compatible_object_storage_provider NOT NULL,
    bucket_name character varying(255) NOT NULL,
    region character varying(255),
    endpoint character varying(2048),
    force_path_style boolean DEFAULT false NOT NULL,
    use_ssl boolean,
    has_session_token boolean DEFAULT false NOT NULL
);



























































































































































































































INSERT INTO public."VersionInfo" VALUES (1, CURRENT_TIMESTAMP, 'Create normalized storage_keys schema and seed default storage config');
INSERT INTO public."VersionInfo" VALUES (2, CURRENT_TIMESTAMP, 'Drop plaintext storage credential columns; secrets now live in OpenBAO');
INSERT INTO public."VersionInfo" VALUES (3, CURRENT_TIMESTAMP, 'Create download orchestration tables: download_jobs, download_job_history, failed_download_jobs, processed_messages');
INSERT INTO public."VersionInfo" VALUES (4, CURRENT_TIMESTAMP, 'Create dedicated ''cleipnir'' schema for Cleipnir.Flows runtime tables');
INSERT INTO public."VersionInfo" VALUES (5, CURRENT_TIMESTAMP, 'Create media source/content version tables for download idempotency');
INSERT INTO public."VersionInfo" VALUES (6, CURRENT_TIMESTAMP, 'Create metadata schema with accounts, media metadata, technical detail, taxonomy, and junction tables');
INSERT INTO public."VersionInfo" VALUES (7, CURRENT_TIMESTAMP, 'Create media.media root table and wire media_guid foreign keys from version + metadata tables');
INSERT INTO public."VersionInfo" VALUES (8, CURRENT_TIMESTAMP, 'Create playlists, playlist_items, playlist_scan_entries, media_playlist_membership in the playlists schema');
INSERT INTO public."VersionInfo" VALUES (9, CURRENT_TIMESTAMP, 'Create playlists.playlist_metadata so media_playlist_membership rows can resolve a playlist''s title without joining playlists.playlists');
INSERT INTO public."VersionInfo" VALUES (10, CURRENT_TIMESTAMP, 'Create download_option_presets table for stored yt-dlp option sets');
INSERT INTO public."VersionInfo" VALUES (11, CURRENT_TIMESTAMP, 'Create scheduled_tasks table');
INSERT INTO public."VersionInfo" VALUES (12, CURRENT_TIMESTAMP, 'Seed monthly scheduler maintenance tasks');
INSERT INTO public."VersionInfo" VALUES (13, CURRENT_TIMESTAMP, 'Create creator source discovery tables');
INSERT INTO public."VersionInfo" VALUES (14, CURRENT_TIMESTAMP, 'Add channel asset cache columns to creator_sources');
INSERT INTO public."VersionInfo" VALUES (15, CURRENT_TIMESTAMP, 'Add info.json sidecar columns to download_jobs');
INSERT INTO public."VersionInfo" VALUES (16, CURRENT_TIMESTAMP, 'Add last_run_status column to scheduled_tasks');
INSERT INTO public."VersionInfo" VALUES (17, CURRENT_TIMESTAMP, 'Seed nightly processed_message_cleanup scheduler task');
INSERT INTO public."VersionInfo" VALUES (18, CURRENT_TIMESTAMP, 'Create filesystem_rescan_findings table');
INSERT INTO public."VersionInfo" VALUES (19, CURRENT_TIMESTAMP, 'Add filesystem rescan path normalization');
INSERT INTO public."VersionInfo" VALUES (20, CURRENT_TIMESTAMP, 'Create orphan cleanup lifecycle table');
INSERT INTO public."VersionInfo" VALUES (21, CURRENT_TIMESTAMP, 'Use the shared storage root for the seeded default local storage');
INSERT INTO public."VersionInfo" VALUES (22, CURRENT_TIMESTAMP, 'Add creator source full scan cursor');
INSERT INTO public."VersionInfo" VALUES (23, CURRENT_TIMESTAMP, 'Add storage_key to media_metadata, media_captions, and accounts for sidecar scoping');
INSERT INTO public."VersionInfo" VALUES (24, CURRENT_TIMESTAMP, 'Drop deprecated avatar/banner cache_path columns from creator_sources');
INSERT INTO public."VersionInfo" VALUES (25, CURRENT_TIMESTAMP, 'Seed weekly channel_asset_refresh scheduler task');
INSERT INTO public."VersionInfo" VALUES (26, CURRENT_TIMESTAMP, 'Create local FrostStream users table');
INSERT INTO public."VersionInfo" VALUES (27, CURRENT_TIMESTAMP, 'Create cookie profile metadata table');
INSERT INTO public."VersionInfo" VALUES (28, CURRENT_TIMESTAMP, 'Add worker_tag column to storage_keys');
INSERT INTO public."VersionInfo" VALUES (29, CURRENT_TIMESTAMP, 'Add meta_storage_path column to download_jobs');
INSERT INTO public."VersionInfo" VALUES (30, CURRENT_TIMESTAMP, 'Add priority column to download_jobs');
INSERT INTO public."VersionInfo" VALUES (31, CURRENT_TIMESTAMP, 'Create local media import tracking tables and ingest origin markers');
INSERT INTO public."VersionInfo" VALUES (32, CURRENT_TIMESTAMP, 'Add cancellation states to download jobs');
INSERT INTO public."VersionInfo" VALUES (33, CURRENT_TIMESTAMP, 'Create owner-scoped user playlists');
INSERT INTO public."VersionInfo" VALUES (34, CURRENT_TIMESTAMP, 'Add text_content column to media_captions for full-text subtitle search');
INSERT INTO public."VersionInfo" VALUES (35, CURRENT_TIMESTAMP, 'Create cached audio rendition table');
INSERT INTO public."VersionInfo" VALUES (36, CURRENT_TIMESTAMP, 'Add playlist audio stream options');
INSERT INTO public."VersionInfo" VALUES (37, CURRENT_TIMESTAMP, 'Create user-scoped download configuration sets');
INSERT INTO public."VersionInfo" VALUES (38, CURRENT_TIMESTAMP, 'Add resolved download config fields to playlists');
INSERT INTO public."VersionInfo" VALUES (39, CURRENT_TIMESTAMP, 'Create owner-scoped searchable notes');
INSERT INTO public."VersionInfo" VALUES (40, CURRENT_TIMESTAMP, 'Add ''ignored'' download job state for keyword-suppressed videos');
INSERT INTO public."VersionInfo" VALUES (41, CURRENT_TIMESTAMP, 'Add ignore keywords to config sets and ignored-keyword reporting columns');
INSERT INTO public."VersionInfo" VALUES (42, CURRENT_TIMESTAMP, 'Add ''provider_halted'' download job state for provider bot detection');
INSERT INTO public."VersionInfo" VALUES (43, CURRENT_TIMESTAMP, 'Add provider-halt retry scheduling columns to download jobs');
INSERT INTO public."VersionInfo" VALUES (44, CURRENT_TIMESTAMP, 'Create watch states and watched-item auto-delete policy');
INSERT INTO public."VersionInfo" VALUES (45, CURRENT_TIMESTAMP, 'Add provider-specific query limits to creator sources');
INSERT INTO public."VersionInfo" VALUES (46, CURRENT_TIMESTAMP, 'Create orphan-cleanup policy');
INSERT INTO public."VersionInfo" VALUES (47, CURRENT_TIMESTAMP, 'Create media access control tables');
INSERT INTO public."VersionInfo" VALUES (48, CURRENT_TIMESTAMP, 'Seed daily channel_media_list full-rescan sweep task');
INSERT INTO public."VersionInfo" VALUES (49, CURRENT_TIMESTAMP, 'Seed channel_update_check, filesystem_rescan, search_reindex and backup scheduler tasks');
INSERT INTO public."VersionInfo" VALUES (50, CURRENT_TIMESTAMP, 'Add per-source incremental update-check interval to creator sources');
INSERT INTO public."VersionInfo" VALUES (51, CURRENT_TIMESTAMP, 'Retick channel-scan-refresh sweep to every 30 minutes');
INSERT INTO public."VersionInfo" VALUES (52, CURRENT_TIMESTAMP, 'Create local media import sessions');
INSERT INTO public."VersionInfo" VALUES (53, CURRENT_TIMESTAMP, 'Create per-user media likes');
INSERT INTO public."VersionInfo" VALUES (54, CURRENT_TIMESTAMP, 'Drop unused provider-halt retry scheduling columns from download jobs');
INSERT INTO public."VersionInfo" VALUES (55, CURRENT_TIMESTAMP, 'Store caption files by location; keep searchable text only in Typesense');
INSERT INTO public."VersionInfo" VALUES (56, CURRENT_TIMESTAMP, 'Add index on media_source_versions.latest_job_id');
INSERT INTO public."VersionInfo" VALUES (57, CURRENT_TIMESTAMP, 'Create download_job_progress_log table');
INSERT INTO public."VersionInfo" VALUES (58, CURRENT_TIMESTAMP, 'Drop encode_for_playlist/audio_format/fetch_comments from download_config_sets');
INSERT INTO public."VersionInfo" VALUES (59, CURRENT_TIMESTAMP, 'Create metadata account external id aliases');
INSERT INTO public."VersionInfo" VALUES (60, CURRENT_TIMESTAMP, 'Create cached stream (video HLS) rendition table');
INSERT INTO public."VersionInfo" VALUES (61, CURRENT_TIMESTAMP, 'Make audio renditions opus-only; drop format columns and enum');
INSERT INTO public."VersionInfo" VALUES (62, CURRENT_TIMESTAMP, 'Mark existing playlist item jobs as playlist downloads');
INSERT INTO public."VersionInfo" VALUES (63, CURRENT_TIMESTAMP, 'Replace the legacy download saga state with Download Flow V2');
INSERT INTO public."VersionInfo" VALUES (64, CURRENT_TIMESTAMP, 'Add import wizard metadata source and fetch state');
INSERT INTO public."VersionInfo" VALUES (65, CURRENT_TIMESTAMP, 'Add import session delete-source-files option');
INSERT INTO public."VersionInfo" VALUES (66, CURRENT_TIMESTAMP, 'Add mount path for mount-backed NFS, SMB, and CIFS storage');
INSERT INTO public."VersionInfo" VALUES (67, CURRENT_TIMESTAMP, 'Create unified access policies');
INSERT INTO public."VersionInfo" VALUES (68, CURRENT_TIMESTAMP, 'Drop legacy media access allow-list tables');
INSERT INTO public."VersionInfo" VALUES (69, CURRENT_TIMESTAMP, 'Remove watched-item auto-delete capability');
INSERT INTO public."VersionInfo" VALUES (70, CURRENT_TIMESTAMP, 'Drop orphan cleanup feature');
INSERT INTO public."VersionInfo" VALUES (71, CURRENT_TIMESTAMP, 'Seed one schedule for each missing registered task type');
INSERT INTO public."VersionInfo" VALUES (72, CURRENT_TIMESTAMP, 'Remove filesystem rescan feature');
INSERT INTO public."VersionInfo" VALUES (73, CURRENT_TIMESTAMP, 'Create durable statistics tables decoupled from download_jobs retention');
INSERT INTO public."VersionInfo" VALUES (74, CURRENT_TIMESTAMP, 'Rename channel_update_check/channel_media_list/stale_database_cleanup task types');
INSERT INTO public."VersionInfo" VALUES (75, CURRENT_TIMESTAMP, 'Move download job tables and playlist download tables into a jobs schema');
INSERT INTO public."VersionInfo" VALUES (76, CURRENT_TIMESTAMP, 'Split jobs.playlists source-descriptor columns into jobs.playlist_source_metadata');
INSERT INTO public."VersionInfo" VALUES (77, CURRENT_TIMESTAMP, 'Split creator_sources scan/asset job state into jobs.creator_scan_state');
INSERT INTO public."VersionInfo" VALUES (78, CURRENT_TIMESTAMP, 'Link discovery.creator_sources to metadata.accounts via account_id');
INSERT INTO public."VersionInfo" VALUES (79, CURRENT_TIMESTAMP, 'Seed nightly download_history_cleanup scheduler task');
INSERT INTO public."VersionInfo" VALUES (80, CURRENT_TIMESTAMP, 'Detach playlist library metadata from download job history');
INSERT INTO public."VersionInfo" VALUES (81, CURRENT_TIMESTAMP, 'Add download history cleanup schedule options');
INSERT INTO public."VersionInfo" VALUES (82, CURRENT_TIMESTAMP, 'Seed nightly import_session_cleanup scheduler task');
INSERT INTO public."VersionInfo" VALUES (83, CURRENT_TIMESTAMP, 'Remove nightly processed_message_cleanup schedule');
INSERT INTO public."VersionInfo" VALUES (84, CURRENT_TIMESTAMP, 'Align scheduled task types with schedule key mappings');
INSERT INTO public."VersionInfo" VALUES (85, CURRENT_TIMESTAMP, 'Seed snapshot and full backup schedules');
INSERT INTO public."VersionInfo" VALUES (86, CURRENT_TIMESTAMP, 'Rename scheduled task keys to canonical task-oriented names');
INSERT INTO public."VersionInfo" VALUES (87, CURRENT_TIMESTAMP, 'Create durable per-media audio encoding status table');
INSERT INTO public."VersionInfo" VALUES (88, CURRENT_TIMESTAMP, 'Add worker_tag to download config sets and playlists');
INSERT INTO public."VersionInfo" VALUES (89, CURRENT_TIMESTAMP, 'Remove creator source provider query limits');
INSERT INTO public."VersionInfo" VALUES (90, CURRENT_TIMESTAMP, 'Link creator sources to download config sets');
INSERT INTO public."VersionInfo" VALUES (91, CURRENT_TIMESTAMP, 'Rename backup-snapshot schedule to backup-diff');
INSERT INTO public."VersionInfo" VALUES (92, CURRENT_TIMESTAMP, 'Drop legacy download flow reset table');
INSERT INTO public."VersionInfo" VALUES (93, CURRENT_TIMESTAMP, 'Add platform/discovery_status index to discovered_media for coverage summary aggregates');
INSERT INTO public."VersionInfo" VALUES (94, CURRENT_TIMESTAMP, 'Create durable per-creator-source and per-account download state ledgers');
INSERT INTO public."VersionInfo" VALUES (95, CURRENT_TIMESTAMP, 'Add direct NFS, SMB, and CIFS connection settings');
INSERT INTO public."VersionInfo" VALUES (96, CURRENT_TIMESTAMP, 'Add metadata.media_live_chat ingestion marker table');
INSERT INTO public."VersionInfo" VALUES (97, CURRENT_TIMESTAMP, 'Add ''live_chat_upload'' download stage for live chat replay artifacts');
INSERT INTO public."VersionInfo" VALUES (98, CURRENT_TIMESTAMP, 'Persist download startup generation boundary');






INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (2, 'db-maintenance', 'db-maintenance', '0 0 3 1 * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (3, 'db-stale-media-cleanup', 'db-stale-media-cleanup', '0 30 3 1 * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (5, 'channel-asset-refresh', 'channel-asset-refresh', '0 0 4 ? * SUN', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (10, 'search-reindex', 'search-reindex', '0 30 5 ? * SUN', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (8, 'channel-scan-refresh', 'channel_scan_refresh', '0 0/30 * * * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (7, 'channel-full-rescan', 'channel_scan_full', '0 30 3 * * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (12, 'download-history-cleanup', 'download-history-cleanup', '0 45 3 * * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (13, 'import-session-cleanup', 'import_session_cleanup', '0 0 4 * * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, 30, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (11, 'backup-diff', 'backup-diff', '0 0 2 * * ?', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, 0, false);
INSERT INTO scheduling.scheduled_tasks OVERRIDING SYSTEM VALUE VALUES (15, 'backup-full', 'backup-full', '0 0 3 ? * SUN', NULL, 'UTC', true, 'Coalesce', NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, 0, false);















INSERT INTO storage.storage_keys OVERRIDING SYSTEM VALUE VALUES (1, 'default', 'Local', 'Fallback/Default Local Storage', CURRENT_TIMESTAMP, NULL, NULL);



INSERT INTO storage.storage_keys_local VALUES (1, 'local', '${FROSTSTREAM_STORAGE_ROOT}');















SELECT pg_catalog.setval('discovery.creator_sources_id_seq', 1, false);



SELECT pg_catalog.setval('discovery.discovered_media_id_seq', 1, false);



SELECT pg_catalog.setval('downloads.download_config_sets_id_seq', 1, false);



SELECT pg_catalog.setval('downloads.download_option_presets_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.download_artifacts_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.download_job_history_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.download_job_progress_log_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.download_job_warnings_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.download_stage_attempts_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.playlist_items_id_seq', 1, false);



SELECT pg_catalog.setval('jobs.playlist_scan_entries_id_seq', 1, false);



SELECT pg_catalog.setval('media.media_content_id_versions_id_seq', 1, false);



SELECT pg_catalog.setval('media.media_source_versions_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.account_external_ids_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.accounts_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.artists_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.audio_stream_details_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.cast_members_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.categories_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.chapter_data_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.genres_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.media_base_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.media_captions_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.media_comments_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.media_format_details_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.media_metadata_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.media_streams_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.music_metadata_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.series_metadata_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.tags_id_seq', 1, false);



SELECT pg_catalog.setval('metadata.video_stream_details_id_seq', 1, false);



SELECT pg_catalog.setval('notes.user_notes_id_seq', 1, false);



SELECT pg_catalog.setval('playlists.media_playlist_membership_id_seq', 1, false);



SELECT pg_catalog.setval('playlists.playlist_metadata_id_seq', 1, false);



SELECT pg_catalog.setval('playlists.user_playlist_items_id_seq', 1, false);



SELECT pg_catalog.setval('scheduling.scheduled_tasks_id_seq', 17, true);



SELECT pg_catalog.setval('statistics.channel_media_downloads_id_seq', 1, false);



SELECT pg_catalog.setval('storage.storage_keys_id_seq', 1, true);



ALTER TABLE ONLY auth.access_policies
    ADD CONSTRAINT "PK_access_policies" PRIMARY KEY (policy_id);



ALTER TABLE ONLY auth.cookie_profiles
    ADD CONSTRAINT "PK_cookie_profiles" PRIMARY KEY (id);



ALTER TABLE ONLY auth.froststream_users
    ADD CONSTRAINT "PK_froststream_users" PRIMARY KEY (id);



ALTER TABLE ONLY auth.access_policy_age_tiers
    ADD CONSTRAINT pk_access_policy_age_tiers PRIMARY KEY (policy_id, minimum_age);



ALTER TABLE ONLY auth.access_policy_assignments
    ADD CONSTRAINT pk_access_policy_assignments PRIMARY KEY (policy_id, principal_type, principal_id);



ALTER TABLE ONLY auth.access_policy_bundles
    ADD CONSTRAINT pk_access_policy_bundles PRIMARY KEY (policy_id, bundle_id);



ALTER TABLE ONLY auth.access_policy_media
    ADD CONSTRAINT pk_access_policy_media PRIMARY KEY (policy_id, media_guid);



ALTER TABLE ONLY auth.access_policy_providers
    ADD CONSTRAINT pk_access_policy_providers PRIMARY KEY (policy_id, provider);



ALTER TABLE ONLY discovery.creator_sources
    ADD CONSTRAINT "PK_creator_sources" PRIMARY KEY (id);



ALTER TABLE ONLY discovery.discovered_media
    ADD CONSTRAINT "PK_discovered_media" PRIMARY KEY (id);



ALTER TABLE ONLY discovery.creator_sources
    ADD CONSTRAINT uq_creator_sources_source_url UNIQUE (source_url);



ALTER TABLE ONLY downloads.download_config_sets
    ADD CONSTRAINT "PK_download_config_sets" PRIMARY KEY (id);



ALTER TABLE ONLY downloads.download_option_presets
    ADD CONSTRAINT "PK_download_option_presets" PRIMARY KEY (id);



ALTER TABLE ONLY downloads.download_config_sets
    ADD CONSTRAINT uq_download_config_sets_owner_key UNIQUE (owner_subject, key);



ALTER TABLE ONLY downloads.download_option_presets
    ADD CONSTRAINT uq_download_option_presets_key UNIQUE (key);



ALTER TABLE ONLY imports.import_session_items
    ADD CONSTRAINT "PK_import_session_items" PRIMARY KEY (item_id);



ALTER TABLE ONLY imports.import_session_mappings
    ADD CONSTRAINT "PK_import_session_mappings" PRIMARY KEY (mapping_id);



ALTER TABLE ONLY imports.import_sessions
    ADD CONSTRAINT "PK_import_sessions" PRIMARY KEY (session_id);



ALTER TABLE ONLY imports.local_import_batches
    ADD CONSTRAINT "PK_local_import_batches" PRIMARY KEY (batch_id);



ALTER TABLE ONLY imports.local_import_items
    ADD CONSTRAINT "PK_local_import_items" PRIMARY KEY (item_id);



ALTER TABLE ONLY jobs.creator_scan_state
    ADD CONSTRAINT "PK_creator_scan_state" PRIMARY KEY (creator_source_id);



ALTER TABLE ONLY jobs.download_artifacts
    ADD CONSTRAINT "PK_download_artifacts" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.download_groups
    ADD CONSTRAINT "PK_download_groups" PRIMARY KEY (group_id);



ALTER TABLE ONLY jobs.download_job_history
    ADD CONSTRAINT "PK_download_job_history" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.download_job_progress_log
    ADD CONSTRAINT "PK_download_job_progress_log" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.download_job_runs
    ADD CONSTRAINT "PK_download_job_runs" PRIMARY KEY (run_id);



ALTER TABLE ONLY jobs.download_job_warnings
    ADD CONSTRAINT "PK_download_job_warnings" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.download_jobs
    ADD CONSTRAINT "PK_download_jobs" PRIMARY KEY (job_id);



ALTER TABLE ONLY jobs.download_provider_circuits
    ADD CONSTRAINT "PK_download_provider_circuits" PRIMARY KEY (provider);



ALTER TABLE ONLY jobs.download_stage_attempts
    ADD CONSTRAINT "PK_download_stage_attempts" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.download_worker_leases
    ADD CONSTRAINT "PK_download_worker_leases" PRIMARY KEY (dispatch_id);



ALTER TABLE ONLY jobs.failed_download_jobs
    ADD CONSTRAINT "PK_failed_download_jobs" PRIMARY KEY (job_id);



ALTER TABLE ONLY jobs.playlist_items
    ADD CONSTRAINT "PK_playlist_items" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.playlist_scan_entries
    ADD CONSTRAINT "PK_playlist_scan_entries" PRIMARY KEY (id);



ALTER TABLE ONLY jobs.playlist_source_metadata
    ADD CONSTRAINT "PK_playlist_source_metadata" PRIMARY KEY (playlist_id);



ALTER TABLE ONLY jobs.playlists
    ADD CONSTRAINT "PK_playlists" PRIMARY KEY (playlist_id);



ALTER TABLE ONLY jobs.processed_messages
    ADD CONSTRAINT "PK_processed_messages" PRIMARY KEY (message_id);



ALTER TABLE ONLY jobs.download_startup_state
    ADD CONSTRAINT download_startup_state_pkey PRIMARY KEY (name);



ALTER TABLE ONLY media.audio_encoding_status
    ADD CONSTRAINT "PK_audio_encoding_status" PRIMARY KEY (media_guid);



ALTER TABLE ONLY media.audio_renditions
    ADD CONSTRAINT "PK_audio_renditions" PRIMARY KEY (rendition_id);



ALTER TABLE ONLY media.media
    ADD CONSTRAINT "PK_media" PRIMARY KEY (media_guid);



ALTER TABLE ONLY media.media_content_id_versions
    ADD CONSTRAINT "PK_media_content_id_versions" PRIMARY KEY (id);



ALTER TABLE ONLY media.media_source_versions
    ADD CONSTRAINT "PK_media_source_versions" PRIMARY KEY (id);



ALTER TABLE ONLY media.stream_renditions
    ADD CONSTRAINT "PK_stream_renditions" PRIMARY KEY (rendition_id);



ALTER TABLE ONLY media.user_media_likes
    ADD CONSTRAINT pk_user_media_likes PRIMARY KEY (owner_subject, media_guid);



ALTER TABLE ONLY media.watch_states
    ADD CONSTRAINT pk_watch_states PRIMARY KEY (owner_subject, media_guid);



ALTER TABLE ONLY metadata.account_external_ids
    ADD CONSTRAINT "PK_account_external_ids" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.accounts
    ADD CONSTRAINT "PK_accounts" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.artists
    ADD CONSTRAINT "PK_artists" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.audio_stream_details
    ADD CONSTRAINT "PK_audio_stream_details" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.cast_members
    ADD CONSTRAINT "PK_cast_members" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.categories
    ADD CONSTRAINT "PK_categories" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.chapter_data
    ADD CONSTRAINT "PK_chapter_data" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.genres
    ADD CONSTRAINT "PK_genres" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_base
    ADD CONSTRAINT "PK_media_base" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_captions
    ADD CONSTRAINT "PK_media_captions" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_comments
    ADD CONSTRAINT "PK_media_comments" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_format_details
    ADD CONSTRAINT "PK_media_format_details" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_live_chat
    ADD CONSTRAINT "PK_media_live_chat" PRIMARY KEY (media_guid);



ALTER TABLE ONLY metadata.media_metadata
    ADD CONSTRAINT "PK_media_metadata" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_streams
    ADD CONSTRAINT "PK_media_streams" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.music_metadata
    ADD CONSTRAINT "PK_music_metadata" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.series_metadata
    ADD CONSTRAINT "PK_series_metadata" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.tags
    ADD CONSTRAINT "PK_tags" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.video_stream_details
    ADD CONSTRAINT "PK_video_stream_details" PRIMARY KEY (id);



ALTER TABLE ONLY metadata.media_artists
    ADD CONSTRAINT pk_media_artists PRIMARY KEY (media_metadata_id, artist_id);



ALTER TABLE ONLY metadata.media_cast
    ADD CONSTRAINT pk_media_cast PRIMARY KEY (media_metadata_id, cast_member_id);



ALTER TABLE ONLY metadata.media_categories
    ADD CONSTRAINT pk_media_categories PRIMARY KEY (media_metadata_id, category_id);



ALTER TABLE ONLY metadata.media_genres
    ADD CONSTRAINT pk_media_genres PRIMARY KEY (media_metadata_id, genre_id);



ALTER TABLE ONLY metadata.media_tags
    ADD CONSTRAINT pk_media_tags PRIMARY KEY (media_metadata_id, tag_id);



ALTER TABLE ONLY notes.user_notes
    ADD CONSTRAINT "PK_user_notes" PRIMARY KEY (id);



ALTER TABLE ONLY notes.user_notes
    ADD CONSTRAINT ux_user_notes_owner_target UNIQUE (owner_subject, target_type, target_id);



ALTER TABLE ONLY playlists.media_playlist_membership
    ADD CONSTRAINT "PK_media_playlist_membership" PRIMARY KEY (id);



ALTER TABLE ONLY playlists.playlist_metadata
    ADD CONSTRAINT "PK_playlist_metadata" PRIMARY KEY (id);



ALTER TABLE ONLY playlists.user_playlist_items
    ADD CONSTRAINT "PK_user_playlist_items" PRIMARY KEY (id);



ALTER TABLE ONLY playlists.user_playlists
    ADD CONSTRAINT "PK_user_playlists" PRIMARY KEY (playlist_id);



ALTER TABLE ONLY public.search_pending_index
    ADD CONSTRAINT search_pending_index_pkey PRIMARY KEY (media_guid);



ALTER TABLE ONLY scheduling.scheduled_tasks
    ADD CONSTRAINT "PK_scheduled_tasks" PRIMARY KEY (id);



ALTER TABLE ONLY scheduling.scheduled_tasks
    ADD CONSTRAINT uq_scheduled_tasks_key UNIQUE (key);



ALTER TABLE ONLY statistics.channel_media_downloads
    ADD CONSTRAINT "PK_channel_media_downloads" PRIMARY KEY (id);



ALTER TABLE ONLY statistics.account_daily_states
    ADD CONSTRAINT pk_account_daily_states PRIMARY KEY (day, account_id, state);



ALTER TABLE ONLY statistics.creator_source_daily_states
    ADD CONSTRAINT pk_creator_source_daily_states PRIMARY KEY (day, creator_source_id, state);



ALTER TABLE ONLY statistics.download_daily_activity
    ADD CONSTRAINT pk_download_daily_activity PRIMARY KEY (day, state);



ALTER TABLE ONLY storage.storage_keys
    ADD CONSTRAINT "PK_storage_keys" PRIMARY KEY (id);



ALTER TABLE ONLY storage.storage_keys_local
    ADD CONSTRAINT "PK_storage_keys_local" PRIMARY KEY (storage_key_id);



ALTER TABLE ONLY storage.storage_keys_network
    ADD CONSTRAINT "PK_storage_keys_network" PRIMARY KEY (storage_key_id);



ALTER TABLE ONLY storage.storage_keys_object_azure_blob
    ADD CONSTRAINT "PK_storage_keys_object_azure_blob" PRIMARY KEY (storage_key_id);



ALTER TABLE ONLY storage.storage_keys_object_google_cloud_storage
    ADD CONSTRAINT "PK_storage_keys_object_google_cloud_storage" PRIMARY KEY (storage_key_id);



ALTER TABLE ONLY storage.storage_keys_object_s3_compatible
    ADD CONSTRAINT "PK_storage_keys_object_s3_compatible" PRIMARY KEY (storage_key_id);



ALTER TABLE ONLY storage.storage_keys
    ADD CONSTRAINT uq_storage_keys_key UNIQUE (key);



CREATE INDEX ix_access_policy_age_minimum ON auth.access_policy_age_tiers USING btree (minimum_age);



CREATE INDEX ix_access_policy_assignments_principal ON auth.access_policy_assignments USING btree (principal_type, principal_id);



CREATE INDEX ix_access_policy_media_guid ON auth.access_policy_media USING btree (media_guid);



CREATE INDEX ix_access_policy_providers_provider ON auth.access_policy_providers USING btree (provider);



CREATE UNIQUE INDEX ux_access_policies_name ON auth.access_policies USING btree (lower((name)::text));



CREATE UNIQUE INDEX ux_cookie_profiles_owner_profile ON auth.cookie_profiles USING btree (owner_subject, profile_key);



CREATE UNIQUE INDEX ux_froststream_users_authentik_subject_id ON auth.froststream_users USING btree (authentik_subject_id);



CREATE INDEX ix_creator_sources_account_id ON discovery.creator_sources USING btree (account_id);



CREATE INDEX ix_creator_sources_scan_enabled ON discovery.creator_sources USING btree (scan_enabled);



CREATE INDEX ix_discovered_media_creator_source_id ON discovery.discovered_media USING btree (creator_source_id);



CREATE INDEX ix_discovered_media_metadata_status ON discovery.discovered_media USING btree (metadata_status);



CREATE INDEX ix_discovered_media_platform_discovery_status ON discovery.discovered_media USING btree (platform, discovery_status);



CREATE UNIQUE INDEX ux_discovered_media_identity ON discovery.discovered_media USING btree (platform, extractor, external_media_id);



CREATE INDEX ix_import_session_items_session_item_id ON imports.import_session_items USING btree (session_id, item_id);



CREATE INDEX ix_import_session_items_session_metadata_state ON imports.import_session_items USING btree (session_id, metadata_state) WHERE (excluded = false);



CREATE INDEX ix_import_session_items_session_status ON imports.import_session_items USING btree (session_id, status);



CREATE INDEX ix_import_sessions_status_updated_at ON imports.import_sessions USING btree (status, updated_at DESC);



CREATE INDEX ix_local_import_batches_status_updated_at ON imports.local_import_batches USING btree (status, updated_at);



CREATE INDEX ix_local_import_items_batch_status ON imports.local_import_items USING btree (batch_id, status);



CREATE UNIQUE INDEX ux_import_session_items_session_relative_path ON imports.import_session_items USING btree (session_id, relative_path);



CREATE UNIQUE INDEX ux_local_import_items_batch_item_index ON imports.local_import_items USING btree (batch_id, item_index);



CREATE INDEX ix_download_job_history_job_id_recorded_at ON jobs.download_job_history USING btree (job_id, recorded_at);



CREATE INDEX ix_download_job_progress_log_job_id_recorded_at ON jobs.download_job_progress_log USING btree (job_id, recorded_at);



CREATE INDEX ix_download_job_warnings_run_stage ON jobs.download_job_warnings USING btree (run_id, stage, artifact_key);



CREATE INDEX ix_download_jobs_correlation_id ON jobs.download_jobs USING btree (correlation_id);



CREATE INDEX ix_download_jobs_priority ON jobs.download_jobs USING btree (state, priority DESC, created_at);



CREATE INDEX ix_download_jobs_state_updated_at ON jobs.download_jobs USING btree (state, updated_at);



CREATE INDEX ix_download_jobs_status_updated_at ON jobs.download_jobs USING btree (status, updated_at);



CREATE INDEX ix_download_worker_leases_status_expiry ON jobs.download_worker_leases USING btree (status, expires_at);



CREATE INDEX ix_playlist_items_playlist_id_index ON jobs.playlist_items USING btree (playlist_id, playlist_index);



CREATE INDEX ix_playlist_scan_entries_playlist_id_index ON jobs.playlist_scan_entries USING btree (playlist_id, playlist_index);



CREATE INDEX ix_playlists_correlation_id ON jobs.playlists USING btree (correlation_id);



CREATE INDEX ix_playlists_state_updated_at ON jobs.playlists USING btree (state, updated_at);



CREATE INDEX ix_processed_messages_job_id ON jobs.processed_messages USING btree (job_id);



CREATE INDEX ix_processed_messages_operation_key ON jobs.processed_messages USING btree (operation_key);



CREATE UNIQUE INDEX ux_download_artifacts_run_key ON jobs.download_artifacts USING btree (run_id, artifact_key);



CREATE UNIQUE INDEX ux_download_groups_correlation_id ON jobs.download_groups USING btree (correlation_id);



CREATE UNIQUE INDEX ux_download_job_runs_job_run_number ON jobs.download_job_runs USING btree (job_id, run_number);



CREATE UNIQUE INDEX ux_download_stage_attempt ON jobs.download_stage_attempts USING btree (run_id, stage, artifact_key, attempt);



CREATE UNIQUE INDEX ux_download_stage_attempt_dispatch ON jobs.download_stage_attempts USING btree (dispatch_id);



CREATE UNIQUE INDEX ux_playlist_items_job_id ON jobs.playlist_items USING btree (job_id);



CREATE UNIQUE INDEX ux_playlist_items_playlist_id_entry_url ON jobs.playlist_items USING btree (playlist_id, entry_url);



CREATE UNIQUE INDEX ux_playlists_source_url ON jobs.playlists USING btree (source_url);



CREATE INDEX ix_audio_encoding_status_account_encoded ON media.audio_encoding_status USING btree (account_id, is_encoded);



CREATE INDEX ix_audio_renditions_status ON media.audio_renditions USING btree (status);



CREATE INDEX ix_media_source_versions_latest_job_id ON media.media_source_versions USING btree (latest_job_id);



CREATE INDEX ix_media_source_versions_media_guid ON media.media_source_versions USING btree (media_guid);



CREATE INDEX ix_media_source_versions_provider_source_media_id ON media.media_source_versions USING btree (provider, source_media_id);



CREATE INDEX ix_stream_renditions_status ON media.stream_renditions USING btree (status);



CREATE INDEX ix_user_media_likes_owner_liked_at ON media.user_media_likes USING btree (owner_subject, liked_at DESC);



CREATE INDEX ix_watch_states_completed_watched_at ON media.watch_states USING btree (completed, watched_at) WHERE ((completed = true) AND (watched_at IS NOT NULL));



CREATE UNIQUE INDEX ux_audio_renditions_media_version_storage ON media.audio_renditions USING btree (media_guid, source_version_num, storage_key);



CREATE UNIQUE INDEX ux_media_content_id_versions_media_guid_version_num ON media.media_content_id_versions USING btree (media_guid, version_num);



CREATE UNIQUE INDEX ux_media_content_id_versions_storage_key_content_hash ON media.media_content_id_versions USING btree (storage_key, content_hash_xxh128);



CREATE UNIQUE INDEX ux_stream_renditions_media_version_storage ON media.stream_renditions USING btree (media_guid, source_version_num, storage_key);



CREATE INDEX fs_index_caption_projection ON metadata.media_captions USING btree (media_guid, id);



CREATE INDEX fs_index_comment_projection ON metadata.media_comments USING btree (media_guid, id);



CREATE INDEX ix_account_external_ids_account_id ON metadata.account_external_ids USING btree (account_id);



CREATE UNIQUE INDEX ix_audio_stream_details_media_stream_id ON metadata.audio_stream_details USING btree (media_stream_id);



CREATE INDEX ix_chapter_data_media_base_id ON metadata.chapter_data USING btree (media_base_id);



CREATE UNIQUE INDEX ix_media_base_media_guid ON metadata.media_base USING btree (media_guid);



CREATE INDEX ix_media_captions_media_lang_type ON metadata.media_captions USING btree (media_guid, two_digit_language_code, caption_type);



CREATE INDEX ix_media_comments_comment_id ON metadata.media_comments USING btree (comment_id);



CREATE INDEX ix_media_comments_media_time ON metadata.media_comments USING btree (media_guid, comment_timestamp);



CREATE INDEX ix_media_comments_parent_id ON metadata.media_comments USING btree (parent_comment_id);



CREATE UNIQUE INDEX ix_media_format_details_media_base_id ON metadata.media_format_details USING btree (media_base_id);



CREATE INDEX ix_media_metadata_account_id ON metadata.media_metadata USING btree (account_id);



CREATE INDEX ix_media_streams_media_base_id ON metadata.media_streams USING btree (media_base_id);



CREATE UNIQUE INDEX ix_video_stream_details_media_stream_id ON metadata.video_stream_details USING btree (media_stream_id);



CREATE UNIQUE INDEX ux_account_external_ids_platform_value ON metadata.account_external_ids USING btree (platform, external_id);



CREATE UNIQUE INDEX ux_accounts_platform_handle ON metadata.accounts USING btree (platform, account_handle);



CREATE UNIQUE INDEX ux_artists_artist_name ON metadata.artists USING btree (artist_name);



CREATE UNIQUE INDEX ux_cast_members_cast_name ON metadata.cast_members USING btree (cast_name);



CREATE UNIQUE INDEX ux_categories_category_name ON metadata.categories USING btree (category_name);



CREATE UNIQUE INDEX ux_genres_genre_name ON metadata.genres USING btree (genre_name);



CREATE UNIQUE INDEX ux_media_metadata_media_guid ON metadata.media_metadata USING btree (media_guid);



CREATE UNIQUE INDEX ux_music_metadata_media_guid ON metadata.music_metadata USING btree (media_guid);



CREATE UNIQUE INDEX ux_series_metadata_media_guid ON metadata.series_metadata USING btree (media_guid);



CREATE UNIQUE INDEX ux_tags_tag_name ON metadata.tags USING btree (tag_name);



CREATE INDEX ix_user_notes_note_trgm ON notes.user_notes USING gin (note public.gin_trgm_ops);



CREATE INDEX ix_user_notes_owner_updated_at ON notes.user_notes USING btree (owner_subject, updated_at DESC);



CREATE INDEX ix_media_playlist_membership_media_guid ON playlists.media_playlist_membership USING btree (media_guid);



CREATE INDEX ix_user_playlists_owner_created_at ON playlists.user_playlists USING btree (owner_subject, created_at DESC);



CREATE UNIQUE INDEX ux_media_playlist_membership_playlist_id_index ON playlists.media_playlist_membership USING btree (playlist_id, playlist_index);



CREATE UNIQUE INDEX ux_metadata_playlist_metadata_playlist_id ON playlists.playlist_metadata USING btree (playlist_id);



CREATE UNIQUE INDEX ux_user_playlist_items_playlist_media ON playlists.user_playlist_items USING btree (playlist_id, media_guid);



CREATE UNIQUE INDEX ux_user_playlist_items_playlist_position ON playlists.user_playlist_items USING btree (playlist_id, "position");



CREATE UNIQUE INDEX "UC_Version" ON public."VersionInfo" USING btree ("Version");



CREATE INDEX search_pending_index_due ON public.search_pending_index USING btree (available, media_guid, lease_until) WHERE (revision > applied);



CREATE INDEX search_pending_index_failures ON public.search_pending_index USING btree (available, media_guid) WHERE ((revision > applied) AND (attempts > 0));



CREATE INDEX ix_scheduled_tasks_next_due_at ON scheduling.scheduled_tasks USING btree (next_due_at);



CREATE INDEX ix_account_daily_states_account_id ON statistics.account_daily_states USING btree (account_id);



CREATE INDEX ix_channel_media_downloads_account_completed_at ON statistics.channel_media_downloads USING btree (account_id, completed_at);



CREATE INDEX ix_channel_media_downloads_media_guid_completed_at ON statistics.channel_media_downloads USING btree (media_guid, completed_at DESC);



CREATE INDEX ix_creator_source_daily_states_creator_source_id ON statistics.creator_source_daily_states USING btree (creator_source_id);



CREATE TRIGGER fs_index_media_delete AFTER DELETE ON media.media FOR EACH ROW EXECUTE FUNCTION public.fs_index_media_delete();



CREATE TRIGGER fs_index_media_insert AFTER INSERT ON media.media FOR EACH ROW EXECUTE FUNCTION public.fs_index_media_insert();



CREATE TRIGGER fs_index_media_update AFTER UPDATE ON media.media FOR EACH ROW EXECUTE FUNCTION public.fs_index_media_update();



CREATE TRIGGER fs_index_dependency_metadata_accounts_delete BEFORE DELETE ON metadata.accounts FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_accounts_delete();



CREATE TRIGGER fs_index_dependency_metadata_accounts_insert AFTER INSERT ON metadata.accounts FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_accounts_insert();



CREATE TRIGGER fs_index_dependency_metadata_accounts_update AFTER UPDATE ON metadata.accounts FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_accounts_update();



CREATE TRIGGER fs_index_dependency_metadata_artists_delete BEFORE DELETE ON metadata.artists FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_artists_delete();



CREATE TRIGGER fs_index_dependency_metadata_artists_insert AFTER INSERT ON metadata.artists FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_artists_insert();



CREATE TRIGGER fs_index_dependency_metadata_artists_update AFTER UPDATE ON metadata.artists FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_artists_update();



CREATE TRIGGER fs_index_dependency_metadata_audio_stream_details_delete BEFORE DELETE ON metadata.audio_stream_details FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_audio_stream_details_delete();



CREATE TRIGGER fs_index_dependency_metadata_audio_stream_details_insert AFTER INSERT ON metadata.audio_stream_details FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_audio_stream_details_insert();



CREATE TRIGGER fs_index_dependency_metadata_audio_stream_details_update AFTER UPDATE ON metadata.audio_stream_details FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_audio_stream_details_update();



CREATE TRIGGER fs_index_dependency_metadata_categories_delete BEFORE DELETE ON metadata.categories FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_categories_delete();



CREATE TRIGGER fs_index_dependency_metadata_categories_insert AFTER INSERT ON metadata.categories FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_categories_insert();



CREATE TRIGGER fs_index_dependency_metadata_categories_update AFTER UPDATE ON metadata.categories FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_categories_update();



CREATE TRIGGER fs_index_dependency_metadata_genres_delete BEFORE DELETE ON metadata.genres FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_genres_delete();



CREATE TRIGGER fs_index_dependency_metadata_genres_insert AFTER INSERT ON metadata.genres FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_genres_insert();



CREATE TRIGGER fs_index_dependency_metadata_genres_update AFTER UPDATE ON metadata.genres FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_genres_update();



CREATE TRIGGER fs_index_dependency_metadata_media_artists_delete BEFORE DELETE ON metadata.media_artists FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_artists_delete();



CREATE TRIGGER fs_index_dependency_metadata_media_artists_insert AFTER INSERT ON metadata.media_artists FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_artists_insert();



CREATE TRIGGER fs_index_dependency_metadata_media_artists_update AFTER UPDATE ON metadata.media_artists FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_artists_update();



CREATE TRIGGER fs_index_dependency_metadata_media_categories_delete BEFORE DELETE ON metadata.media_categories FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_categories_delete();



CREATE TRIGGER fs_index_dependency_metadata_media_categories_insert AFTER INSERT ON metadata.media_categories FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_categories_insert();



CREATE TRIGGER fs_index_dependency_metadata_media_categories_update AFTER UPDATE ON metadata.media_categories FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_categories_update();



CREATE TRIGGER fs_index_dependency_metadata_media_genres_delete BEFORE DELETE ON metadata.media_genres FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_genres_delete();



CREATE TRIGGER fs_index_dependency_metadata_media_genres_insert AFTER INSERT ON metadata.media_genres FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_genres_insert();



CREATE TRIGGER fs_index_dependency_metadata_media_genres_update AFTER UPDATE ON metadata.media_genres FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_genres_update();



CREATE TRIGGER fs_index_dependency_metadata_media_streams_delete BEFORE DELETE ON metadata.media_streams FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_streams_delete();



CREATE TRIGGER fs_index_dependency_metadata_media_streams_insert AFTER INSERT ON metadata.media_streams FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_streams_insert();



CREATE TRIGGER fs_index_dependency_metadata_media_streams_update AFTER UPDATE ON metadata.media_streams FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_streams_update();



CREATE TRIGGER fs_index_dependency_metadata_media_tags_delete BEFORE DELETE ON metadata.media_tags FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_tags_delete();



CREATE TRIGGER fs_index_dependency_metadata_media_tags_insert AFTER INSERT ON metadata.media_tags FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_tags_insert();



CREATE TRIGGER fs_index_dependency_metadata_media_tags_update AFTER UPDATE ON metadata.media_tags FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_media_tags_update();



CREATE TRIGGER fs_index_dependency_metadata_tags_delete BEFORE DELETE ON metadata.tags FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_tags_delete();



CREATE TRIGGER fs_index_dependency_metadata_tags_insert AFTER INSERT ON metadata.tags FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_tags_insert();



CREATE TRIGGER fs_index_dependency_metadata_tags_update AFTER UPDATE ON metadata.tags FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_tags_update();



CREATE TRIGGER fs_index_dependency_metadata_video_stream_details_delete BEFORE DELETE ON metadata.video_stream_details FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_video_stream_details_delete();



CREATE TRIGGER fs_index_dependency_metadata_video_stream_details_insert AFTER INSERT ON metadata.video_stream_details FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_video_stream_details_insert();



CREATE TRIGGER fs_index_dependency_metadata_video_stream_details_update AFTER UPDATE ON metadata.video_stream_details FOR EACH ROW EXECUTE FUNCTION public.fs_index_dependency_metadata_video_stream_details_update();



ALTER TABLE ONLY auth.access_policy_age_tiers
    ADD CONSTRAINT fk_access_policy_age_policy FOREIGN KEY (policy_id) REFERENCES auth.access_policies(policy_id) ON DELETE CASCADE;



ALTER TABLE ONLY auth.access_policy_assignments
    ADD CONSTRAINT fk_access_policy_assignments_policy FOREIGN KEY (policy_id) REFERENCES auth.access_policies(policy_id) ON DELETE CASCADE;



ALTER TABLE ONLY auth.access_policy_bundles
    ADD CONSTRAINT fk_access_policy_bundles_policy FOREIGN KEY (policy_id) REFERENCES auth.access_policies(policy_id) ON DELETE CASCADE;



ALTER TABLE ONLY auth.access_policy_media
    ADD CONSTRAINT fk_access_policy_media_policy FOREIGN KEY (policy_id) REFERENCES auth.access_policies(policy_id) ON DELETE CASCADE;



ALTER TABLE ONLY auth.access_policy_providers
    ADD CONSTRAINT fk_access_policy_providers_policy FOREIGN KEY (policy_id) REFERENCES auth.access_policies(policy_id) ON DELETE CASCADE;



ALTER TABLE ONLY discovery.creator_sources
    ADD CONSTRAINT fk_creator_sources_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE SET NULL;



ALTER TABLE ONLY discovery.discovered_media
    ADD CONSTRAINT fk_discovered_media_creator_source_id FOREIGN KEY (creator_source_id) REFERENCES discovery.creator_sources(id) ON DELETE CASCADE;



ALTER TABLE ONLY imports.import_session_items
    ADD CONSTRAINT fk_import_session_items_session_id FOREIGN KEY (session_id) REFERENCES imports.import_sessions(session_id) ON DELETE CASCADE;



ALTER TABLE ONLY imports.import_session_mappings
    ADD CONSTRAINT fk_import_session_mappings_session_id FOREIGN KEY (session_id) REFERENCES imports.import_sessions(session_id) ON DELETE CASCADE;



ALTER TABLE ONLY imports.local_import_items
    ADD CONSTRAINT fk_local_import_items_batch_id FOREIGN KEY (batch_id) REFERENCES imports.local_import_batches(batch_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.creator_scan_state
    ADD CONSTRAINT fk_creator_scan_state_creator_source_id FOREIGN KEY (creator_source_id) REFERENCES discovery.creator_sources(id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_artifacts
    ADD CONSTRAINT fk_download_artifacts_run_id FOREIGN KEY (run_id) REFERENCES jobs.download_job_runs(run_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_job_history
    ADD CONSTRAINT fk_download_job_history_job_id FOREIGN KEY (job_id) REFERENCES jobs.download_jobs(job_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_job_progress_log
    ADD CONSTRAINT fk_download_job_progress_log_job_id FOREIGN KEY (job_id) REFERENCES jobs.download_jobs(job_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_job_runs
    ADD CONSTRAINT fk_download_job_runs_job_id FOREIGN KEY (job_id) REFERENCES jobs.download_jobs(job_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_job_warnings
    ADD CONSTRAINT fk_download_job_warnings_run_id FOREIGN KEY (run_id) REFERENCES jobs.download_job_runs(run_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_stage_attempts
    ADD CONSTRAINT fk_download_stage_attempts_run_id FOREIGN KEY (run_id) REFERENCES jobs.download_job_runs(run_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.download_worker_leases
    ADD CONSTRAINT fk_download_worker_leases_run_id FOREIGN KEY (run_id) REFERENCES jobs.download_job_runs(run_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.playlist_items
    ADD CONSTRAINT fk_playlist_items_job_id FOREIGN KEY (job_id) REFERENCES jobs.download_jobs(job_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.playlist_items
    ADD CONSTRAINT fk_playlist_items_playlist_id FOREIGN KEY (playlist_id) REFERENCES jobs.playlists(playlist_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.playlist_scan_entries
    ADD CONSTRAINT fk_playlist_scan_entries_playlist_id FOREIGN KEY (playlist_id) REFERENCES jobs.playlists(playlist_id) ON DELETE CASCADE;



ALTER TABLE ONLY jobs.playlist_source_metadata
    ADD CONSTRAINT fk_playlist_source_metadata_playlist_id FOREIGN KEY (playlist_id) REFERENCES jobs.playlists(playlist_id) ON DELETE CASCADE;



ALTER TABLE ONLY media.audio_encoding_status
    ADD CONSTRAINT fk_audio_encoding_status_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE CASCADE;



ALTER TABLE ONLY media.audio_encoding_status
    ADD CONSTRAINT fk_audio_encoding_status_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY media.audio_renditions
    ADD CONSTRAINT fk_audio_renditions_source_version FOREIGN KEY (media_guid, source_version_num) REFERENCES media.media_content_id_versions(media_guid, version_num) ON DELETE CASCADE;



ALTER TABLE ONLY media.media_content_id_versions
    ADD CONSTRAINT fk_media_content_id_versions_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY media.media_source_versions
    ADD CONSTRAINT fk_media_source_versions_latest_job_id FOREIGN KEY (latest_job_id) REFERENCES jobs.download_jobs(job_id) ON DELETE SET NULL;



ALTER TABLE ONLY media.media_source_versions
    ADD CONSTRAINT fk_media_source_versions_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY media.stream_renditions
    ADD CONSTRAINT fk_stream_renditions_source_version FOREIGN KEY (media_guid, source_version_num) REFERENCES media.media_content_id_versions(media_guid, version_num) ON DELETE CASCADE;



ALTER TABLE ONLY media.user_media_likes
    ADD CONSTRAINT fk_user_media_likes_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY media.watch_states
    ADD CONSTRAINT fk_watch_states_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.account_external_ids
    ADD CONSTRAINT fk_account_external_ids_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.audio_stream_details
    ADD CONSTRAINT fk_audio_stream_details_media_stream_id FOREIGN KEY (media_stream_id) REFERENCES metadata.media_streams(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.chapter_data
    ADD CONSTRAINT fk_chapter_data_media_base_id FOREIGN KEY (media_base_id) REFERENCES metadata.media_base(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_artists
    ADD CONSTRAINT fk_media_artists_artist_id FOREIGN KEY (artist_id) REFERENCES metadata.artists(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_artists
    ADD CONSTRAINT fk_media_artists_media_metadata_id FOREIGN KEY (media_metadata_id) REFERENCES metadata.media_metadata(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_cast
    ADD CONSTRAINT fk_media_cast_cast_member_id FOREIGN KEY (cast_member_id) REFERENCES metadata.cast_members(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_cast
    ADD CONSTRAINT fk_media_cast_media_metadata_id FOREIGN KEY (media_metadata_id) REFERENCES metadata.media_metadata(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_categories
    ADD CONSTRAINT fk_media_categories_category_id FOREIGN KEY (category_id) REFERENCES metadata.categories(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_categories
    ADD CONSTRAINT fk_media_categories_media_metadata_id FOREIGN KEY (media_metadata_id) REFERENCES metadata.media_metadata(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_comments
    ADD CONSTRAINT fk_media_comments_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_format_details
    ADD CONSTRAINT fk_media_format_details_media_base_id FOREIGN KEY (media_base_id) REFERENCES metadata.media_base(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_genres
    ADD CONSTRAINT fk_media_genres_genre_id FOREIGN KEY (genre_id) REFERENCES metadata.genres(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_genres
    ADD CONSTRAINT fk_media_genres_media_metadata_id FOREIGN KEY (media_metadata_id) REFERENCES metadata.media_metadata(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_metadata
    ADD CONSTRAINT fk_media_metadata_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_streams
    ADD CONSTRAINT fk_media_streams_media_base_id FOREIGN KEY (media_base_id) REFERENCES metadata.media_base(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_tags
    ADD CONSTRAINT fk_media_tags_media_metadata_id FOREIGN KEY (media_metadata_id) REFERENCES metadata.media_metadata(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_tags
    ADD CONSTRAINT fk_media_tags_tag_id FOREIGN KEY (tag_id) REFERENCES metadata.tags(id) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_base
    ADD CONSTRAINT fk_metadata_media_base_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_captions
    ADD CONSTRAINT fk_metadata_media_captions_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_comments
    ADD CONSTRAINT fk_metadata_media_comments_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.media_metadata
    ADD CONSTRAINT fk_metadata_media_metadata_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.music_metadata
    ADD CONSTRAINT fk_metadata_music_metadata_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.series_metadata
    ADD CONSTRAINT fk_metadata_series_metadata_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY metadata.video_stream_details
    ADD CONSTRAINT fk_video_stream_details_media_stream_id FOREIGN KEY (media_stream_id) REFERENCES metadata.media_streams(id) ON DELETE CASCADE;



ALTER TABLE ONLY playlists.media_playlist_membership
    ADD CONSTRAINT fk_media_playlist_membership_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY playlists.user_playlist_items
    ADD CONSTRAINT fk_user_playlist_items_media_guid FOREIGN KEY (media_guid) REFERENCES media.media(media_guid) ON DELETE CASCADE;



ALTER TABLE ONLY playlists.user_playlist_items
    ADD CONSTRAINT fk_user_playlist_items_playlist_id FOREIGN KEY (playlist_id) REFERENCES playlists.user_playlists(playlist_id) ON DELETE CASCADE;



ALTER TABLE ONLY statistics.account_daily_states
    ADD CONSTRAINT fk_account_daily_states_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE CASCADE;



ALTER TABLE ONLY statistics.channel_media_downloads
    ADD CONSTRAINT fk_channel_media_downloads_account_id FOREIGN KEY (account_id) REFERENCES metadata.accounts(id) ON DELETE CASCADE;



ALTER TABLE ONLY statistics.creator_source_daily_states
    ADD CONSTRAINT fk_creator_source_daily_states_creator_source_id FOREIGN KEY (creator_source_id) REFERENCES discovery.creator_sources(id) ON DELETE CASCADE;



ALTER TABLE ONLY storage.storage_keys_local
    ADD CONSTRAINT fk_storage_keys_local_storage_key_id FOREIGN KEY (storage_key_id) REFERENCES storage.storage_keys(id) ON UPDATE CASCADE ON DELETE CASCADE;



ALTER TABLE ONLY storage.storage_keys_network
    ADD CONSTRAINT fk_storage_keys_network_storage_key_id FOREIGN KEY (storage_key_id) REFERENCES storage.storage_keys(id) ON UPDATE CASCADE ON DELETE CASCADE;



ALTER TABLE ONLY storage.storage_keys_object_azure_blob
    ADD CONSTRAINT fk_storage_keys_object_azure_blob_storage_key_id FOREIGN KEY (storage_key_id) REFERENCES storage.storage_keys(id) ON UPDATE CASCADE ON DELETE CASCADE;



ALTER TABLE ONLY storage.storage_keys_object_google_cloud_storage
    ADD CONSTRAINT fk_storage_keys_object_google_cloud_storage_storage_key_id FOREIGN KEY (storage_key_id) REFERENCES storage.storage_keys(id) ON UPDATE CASCADE ON DELETE CASCADE;



ALTER TABLE ONLY storage.storage_keys_object_s3_compatible
    ADD CONSTRAINT fk_storage_keys_object_s3_compatible_storage_key_id FOREIGN KEY (storage_key_id) REFERENCES storage.storage_keys(id) ON UPDATE CASCADE ON DELETE CASCADE;

CREATE SCHEMA IF NOT EXISTS cleipnir;
