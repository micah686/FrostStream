using System.Collections.Concurrent;
using System.Data.Common;
using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace DataBridge.Persistence;
public interface IPersistenceConnectionFactory
{
    PersistenceDialect Dialect { get; }
    DbConnection OpenConnection();
    Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default);
}
public abstract class PersistenceDialect
{
    public abstract PersistenceProvider Provider { get; }
    public abstract string EfProviderName { get; }
    public abstract DbParameter Parameter(string name, ApplicationParameterType? type, object value);
    public abstract void ConfigureModel(ModelBuilder model);
    public abstract bool IsContention(Exception error);
    public abstract bool IsUniqueViolation(Exception error);
    public virtual string? UniqueConstraint(Exception error) => null;
    public virtual int MutationTimeout(DbConnection connection) => 5;
    public virtual int CommandTimeout(DbConnection connection, int requested) => requested;
    public abstract Task<IDbContextTransaction> BeginAsync(DataBridgeDbContext db, CancellationToken ct);
}
/// <summary>Provider adapters register immutable metadata for their ADO.NET connection type. No services or I/O are created here.</summary>
public static class PersistenceDialects
{
    private static readonly ConcurrentDictionary<Type, PersistenceDialect> Dialects = new();
    public static void Register<TConnection>(PersistenceDialect dialect) where TConnection : DbConnection => Dialects[typeof(TConnection)] = dialect;
    public static PersistenceDialect For(DbConnection connection) => Dialects.TryGetValue(connection.GetType(), out var dialect)
        ? dialect : throw new InvalidOperationException($"No persistence adapter registered for {connection.GetType().Name}.");
    public static PersistenceDialect ForProvider(string? name) => Dialects.Values.FirstOrDefault(d => d.EfProviderName == name)
        ?? throw new InvalidOperationException($"No persistence adapter registered for {name}.");
    public static bool UsesSqlite(this Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db)
        => db.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
    public static bool IsContention(Exception error) => Dialects.Values.Any(d => d.IsContention(error));
    public static bool IsUniqueViolation(Exception error) => Dialects.Values.Any(d => d.IsUniqueViolation(error));
    public static string? UniqueConstraint(Exception error) => Dialects.Values.Select(d => d.UniqueConstraint(error)).FirstOrDefault(c => c is not null);
}
