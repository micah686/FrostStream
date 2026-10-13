using System.Data;
using System.Data.Common;
using DataBridge.Data;
using DataBridge.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
namespace DataBridge.Persistence.Postgres;
public sealed class PostgresDialect : PersistenceDialect
{
    public static PostgresDialect Instance { get; } = new();
    public static void Register() => PersistenceDialects.Register<NpgsqlConnection>(Instance);
    public override PersistenceProvider Provider => PersistenceProvider.Postgres;
    public override string EfProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";
    public override DbParameter Parameter(string name, ApplicationParameterType? type, object value) => type is null ? new NpgsqlParameter(name,value)
        : new NpgsqlParameter(name,type switch { ApplicationParameterType.Text => NpgsqlDbType.Text, ApplicationParameterType.Integer => NpgsqlDbType.Integer,
            ApplicationParameterType.Int64 => NpgsqlDbType.Bigint, ApplicationParameterType.Guid => NpgsqlDbType.Uuid, ApplicationParameterType.TextArray => NpgsqlDbType.Array | NpgsqlDbType.Text, _ => throw new ArgumentOutOfRangeException(nameof(type)) }) { Value = value };
    public override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var guidText = modelBuilder.HasDbFunction(typeof(PersistenceFunctions).GetMethod(nameof(PersistenceFunctions.GuidText))!);
        var like = modelBuilder.HasDbFunction(typeof(PersistenceFunctions).GetMethod(nameof(PersistenceFunctions.ILike))!);
        guidText.HasTranslation(args => new Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlUnaryExpression(
            System.Linq.Expressions.ExpressionType.Convert, args[0], typeof(string), new Microsoft.EntityFrameworkCore.Storage.StringTypeMapping("text", System.Data.DbType.String)));
#pragma warning disable EF1001
        like.HasTranslation(args => new Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgILikeExpression(args[0], args[1], args[2], null));
#pragma warning restore EF1001
        PostgresModelConfiguration.Apply(modelBuilder);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.FindAnnotation(PersistenceModelConfiguration.TypeHint)?.Value is string columnType) property.SetColumnType(columnType);
            if (property.FindAnnotation(PersistenceModelConfiguration.TimestampDefaultHint)?.Value is true) property.SetDefaultValueSql("CURRENT_TIMESTAMP");
        }
    }
    public override bool IsContention(Exception error) => error is PostgresException { SqlState: "40001" or "40P01" or "55P03" };
    public override bool IsUniqueViolation(Exception error) => error is PostgresException { SqlState: "23505" };
    public override string? UniqueConstraint(Exception error) => error is PostgresException { SqlState: "23505" } postgres ? postgres.ConstraintName : null;
    public override async Task<IDbContextTransaction> BeginAsync(DataBridgeDbContext db, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try { await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1000ms'; SET LOCAL statement_timeout = '5000ms';", ct); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
public sealed class PostgresConnectionFactory(NpgsqlDataSource source) : IPersistenceConnectionFactory
{
    public PersistenceDialect Dialect => PostgresDialect.Instance;
    public DbConnection OpenConnection() => source.OpenConnection();
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default) => await source.OpenConnectionAsync(ct);
}
