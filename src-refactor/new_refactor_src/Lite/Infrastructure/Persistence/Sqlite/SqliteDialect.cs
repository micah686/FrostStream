using System.Data;
using System.Data.Common;
using DataBridge.Data;
using DataBridge.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.Sqlite;
namespace DataBridge.Persistence.Sqlite;
public sealed class SqliteDialect : PersistenceDialect
{
    public static SqliteDialect Instance { get; } = new();
    public static void Register() => PersistenceDialects.Register<SqliteConnection>(Instance);
    public override PersistenceProvider Provider => PersistenceProvider.Sqlite;
    public override string EfProviderName => "Microsoft.EntityFrameworkCore.Sqlite";
    public override DbParameter Parameter(string name, ApplicationParameterType? type, object value) => type is null
        ? new SqliteParameter(name, value) : new SqliteParameter(name, type is ApplicationParameterType.Integer or ApplicationParameterType.Int64 ? SqliteType.Integer : SqliteType.Text) { Value = value };
    public override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var guidText = modelBuilder.HasDbFunction(typeof(PersistenceFunctions).GetMethod(nameof(PersistenceFunctions.GuidText))!);
        var like = modelBuilder.HasDbFunction(typeof(PersistenceFunctions).GetMethod(nameof(PersistenceFunctions.ILike))!);
        guidText.HasName("fs_guid_text"); like.HasName("fs_ilike"); SqliteModelConfiguration.Apply(modelBuilder);
    }
    public override bool IsContention(Exception error) => error is SqliteException { SqliteErrorCode: 5 or 6 };
    public override bool IsUniqueViolation(Exception error) => error is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 };
    public override int MutationTimeout(DbConnection connection) => ((SqliteConnection)connection).DefaultTimeout;
    public override int CommandTimeout(DbConnection connection, int requested) => requested == 0 ? MutationTimeout(connection) : Math.Min(requested, MutationTimeout(connection));
    public override async Task<IDbContextTransaction> BeginAsync(DataBridgeDbContext db, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection(); var timeout = connection.DefaultTimeout;
        connection.DefaultTimeout = 1;
        try { return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct); }
        finally { connection.DefaultTimeout = timeout; }
    }
}
