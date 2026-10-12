using System.Data.Common;

namespace DataBridge.Persistence.Indexing;

public interface IIndexSchemaInitializer { void Initialize(); }

public sealed record IndexWork(string MediaGuid,long Revision,string Token,int Attempts=0);
public sealed record IndexFailure(string MediaGuid,long Revision,int Attempts,string Error);

/// <summary>Durable revision fencing. Provider calls happen after the short claim transaction commits.</summary>
public sealed class PendingIndexStore(ApplicationDatabase database)
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public async Task<IndexWork?> ClaimAsync(CancellationToken ct)
    {
        await using var read=database.CreateCommand("SELECT media_guid,revision,attempts FROM search_pending_index WHERE revision>applied AND available<=@now AND lease_until<=@now ORDER BY available,media_guid LIMIT 1");
        read.Parameters.AddWithValue("@now",Now);
        string id; long revision; int attempts;
        await using(var reader=await read.ExecuteReaderAsync(ct)) {
            if(!await reader.ReadAsync(ct)) return null;
            id=reader.GetString(0); revision=reader.GetInt64(1); attempts=reader.GetInt32(2);
        }
        await read.DisposeAsync();
        await using var connection=await database.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        var token=Guid.NewGuid().ToString("N");
        await using var claim=ApplicationDbCommands.Create(connection,"UPDATE search_pending_index SET token=@token,lease_until=@lease WHERE media_guid=@id AND revision=@revision AND revision>applied AND available<=@now AND lease_until<=@now",transaction);
        claim.Parameters.AddWithValue("@token",token); claim.Parameters.AddWithValue("@lease",Now+180000);
        claim.Parameters.AddWithValue("@id",id); claim.Parameters.AddWithValue("@revision",revision); claim.Parameters.AddWithValue("@now",Now);
        if(await claim.ExecuteNonQueryAsync(ct)!=1) return null;
        await transaction.CommitAsync(ct);
        return new(id,revision,token,attempts);
    }
    public Task CompleteAsync(IndexWork work,CancellationToken ct) => UpdateAsync(work,
        "UPDATE search_pending_index SET applied=@revision,token=NULL,lease_until=0,attempts=CASE WHEN revision=@revision THEN 0 ELSE attempts END,last_error=CASE WHEN revision=@revision THEN NULL ELSE last_error END WHERE media_guid=@id AND token=@token AND lease_until>@now",ct);
    public async Task ReleaseAsync(IndexWork work,CancellationToken ct) {
        await using var command=database.CreateCommand("UPDATE search_pending_index SET token=NULL,lease_until=0 WHERE media_guid=@id AND token=@token");
        command.Parameters.AddWithValue("@id",work.MediaGuid); command.Parameters.AddWithValue("@token",work.Token);
        await command.ExecuteNonQueryAsync(ct);
    }
    public Task RenewAsync(IndexWork work,CancellationToken ct) => UpdateAsync(work,
        "UPDATE search_pending_index SET lease_until=@lease WHERE media_guid=@id AND token=@token AND lease_until>@now",ct);
    private async Task UpdateAsync(IndexWork work,string sql,CancellationToken ct) {
        await using var command=database.CreateCommand(sql);
        command.Parameters.AddWithValue("@id",work.MediaGuid); command.Parameters.AddWithValue("@token",work.Token);
        command.Parameters.AddWithValue("@revision",work.Revision); command.Parameters.AddWithValue("@now",Now); command.Parameters.AddWithValue("@lease",Now+180000);
        if(await command.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Index lease was lost.");
    }
    public async Task FailAsync(IndexWork work,Exception error,CancellationToken ct) {
        await using var command=database.CreateCommand("UPDATE search_pending_index SET attempts=CASE WHEN revision=@revision THEN attempts+1 ELSE attempts END,last_error=CASE WHEN revision=@revision THEN @error ELSE last_error END,available=CASE WHEN revision=@revision THEN @available ELSE available END,lease_until=0,token=NULL WHERE media_guid=@id AND token=@token");
        command.Parameters.AddWithValue("@revision",work.Revision);
        command.Parameters.AddWithValue("@error",error.Message[..Math.Min(2048,error.Message.Length)]);
        command.Parameters.AddWithValue("@available",Now+Math.Min(60000,1000L*(1L<<Math.Min(6,work.Attempts)))); command.Parameters.AddWithValue("@id",work.MediaGuid); command.Parameters.AddWithValue("@token",work.Token);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task InvalidateAsync(CancellationToken ct) {
        // Existing tombstones and every current media entity must survive a destructive legacy rebuild.
        await using var connection=await database.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var update=ApplicationDbCommands.Create(connection,"UPDATE search_pending_index SET revision=revision+1,available=0,attempts=0,last_error=NULL",transaction)) await update.ExecuteNonQueryAsync(ct);
        var source=database.Provider==PersistenceProvider.Sqlite ? "media_guid" : "replace(media_guid::text,'-','')";
        await using(var insert=ApplicationDbCommands.Create(connection,$"INSERT INTO search_pending_index(media_guid,revision) SELECT {source},1 FROM {database.Table("media.media")} WHERE 1=1 ON CONFLICT(media_guid) DO NOTHING",transaction)) await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task RequestAsync(Guid mediaGuid,CancellationToken ct=default) {
        await using var command=database.CreateCommand("INSERT INTO search_pending_index(media_guid,revision) VALUES(@id,1) ON CONFLICT(media_guid) DO UPDATE SET revision=search_pending_index.revision+1,available=0,attempts=0,last_error=NULL");
        command.Parameters.AddWithValue("@id",mediaGuid.ToString("N")); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<IndexFailure>> GetFailuresAsync(CancellationToken ct=default) {
        await using var command=database.CreateCommand("SELECT media_guid,revision,attempts,last_error FROM search_pending_index WHERE revision>applied AND attempts>0 ORDER BY available LIMIT 100");
        await using var reader=await command.ExecuteReaderAsync(ct); var result=new List<IndexFailure>();
        while(await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetInt32(2),reader.GetString(3)));
        return result;
    }
}
