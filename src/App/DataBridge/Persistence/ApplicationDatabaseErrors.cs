using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataBridge.Persistence;

public static class ApplicationDatabaseErrors
{
    public static bool IsUniqueViolation(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => true,
        // SQLITE_CONSTRAINT_PRIMARYKEY / SQLITE_CONSTRAINT_UNIQUE; other constraints remain errors.
        SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 } => true,
        DbUpdateException { InnerException: { } inner } => IsUniqueViolation(inner),
        _ => false
    };
}
