#pragma warning disable EF1002 // SQL identifiers come only from the compiled EF model; no user input.
using DataBridge.Data;
using DataBridge.Persistence.Indexing;
using DataBridge.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DataBridge.Persistence.Postgres;

internal sealed class PendingIndexSchema(DataBridgeDbContext db) : DataBridge.Persistence.Indexing.IIndexSchemaInitializer
{
    public void Initialize()
    {
        using var transaction = db.Database.BeginTransaction();
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS search_pending_index(
                media_guid TEXT PRIMARY KEY, revision BIGINT NOT NULL DEFAULT 1, applied BIGINT NOT NULL DEFAULT 0,
                available BIGINT NOT NULL DEFAULT 0, lease_until BIGINT NOT NULL DEFAULT 0, token TEXT,
                attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT);
            CREATE INDEX IF NOT EXISTS search_pending_index_failures ON search_pending_index(available,media_guid) WHERE revision>applied AND attempts>0;
            CREATE INDEX IF NOT EXISTS search_pending_index_due ON search_pending_index(available,media_guid,lease_until) WHERE revision>applied;
            """);
        db.Database.ExecuteSqlRaw($"CREATE INDEX IF NOT EXISTS fs_index_comment_projection ON {ApplicationQueries.Table(PersistenceProvider.Postgres, "metadata.media_comments")}(media_guid,id);");
        db.Database.ExecuteSqlRaw($"CREATE INDEX IF NOT EXISTS fs_index_caption_projection ON {ApplicationQueries.Table(PersistenceProvider.Postgres, "metadata.media_captions")}(media_guid,id);");
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var name = entity.GetTableName(); if (name is null) continue;
            var table = StoreObjectIdentifier.Table(name, entity.GetSchema());
            var property = entity.GetProperties().FirstOrDefault(p => p.GetColumnName(table) == "media_guid");
            // Playback progress, job history and rendition status do not affect these search documents.
            string[] indexed = ["media.media", "metadata.media_metadata", "metadata.media_comments", "metadata.media_captions", "metadata.media_base",
                "media_media", "metadata_media_metadata", "metadata_media_comments", "metadata_media_captions", "metadata_media_base"];
            if (property is null || !(indexed.Contains(entity.GetSchema() + "." + name) || indexed.Contains(name))) continue;
            var physical = "\"" + entity.GetSchema() + "\".\"" + name + "\"";
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var row = operation == "DELETE" ? "OLD" : "NEW";
                var id = "replace(" + row + ".media_guid::text,'-','')";
                var trigger = "fs_index_" + name + "_" + operation.ToLowerInvariant();
                var enqueue = $"INSERT INTO search_pending_index(media_guid,revision) VALUES({id},1) ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;";
                if (operation == "UPDATE") enqueue += enqueue.Replace("NEW.media_guid", "OLD.media_guid");
                db.Database.ExecuteSqlRaw($"CREATE OR REPLACE FUNCTION \"{trigger}\"() RETURNS trigger LANGUAGE plpgsql AS $body$ BEGIN {enqueue} RETURN {row}; END; $body$;");
                db.Database.ExecuteSqlRaw($"DROP TRIGGER IF EXISTS \"{trigger}\" ON {physical}; CREATE TRIGGER \"{trigger}\" AFTER {operation} ON {physical} FOR EACH ROW EXECUTE FUNCTION \"{trigger}\"();");
            }
        }
        foreach (var dependency in IndexProjectionDependencies.All)
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var physical = ApplicationQueries.Table(PersistenceProvider.Postgres, dependency.Table);
                var row = operation == "DELETE" ? "OLD" : "NEW";
                var select = IndexProjectionDependencies.Render(dependency, PersistenceProvider.Postgres, row);
                var normalized = "replace(media_guid::text,'-','')";
                var enqueue = $"INSERT INTO search_pending_index(media_guid,revision) SELECT DISTINCT {normalized},1 FROM ({select}) projection WHERE media_guid IS NOT NULL ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL;";
                if (operation == "UPDATE") enqueue += enqueue.Replace("NEW.", "OLD.");
                var trigger = "fs_index_dependency_" + dependency.Table.Replace('.', '_') + "_" + operation.ToLowerInvariant();
                var timing = operation == "DELETE" ? "BEFORE" : "AFTER";
                db.Database.ExecuteSqlRaw($"CREATE OR REPLACE FUNCTION \"{trigger}\"() RETURNS trigger LANGUAGE plpgsql AS $body$ BEGIN {enqueue} RETURN {row}; END; $body$;");
                db.Database.ExecuteSqlRaw($"DROP TRIGGER IF EXISTS \"{trigger}\" ON {physical}; CREATE TRIGGER \"{trigger}\" {timing} {operation} ON {physical} FOR EACH ROW EXECUTE FUNCTION \"{trigger}\"();");
            }
        transaction.Commit();
    }
}
