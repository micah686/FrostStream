using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataBridge.Data;

internal static class DbExceptionHelpers
{
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
               || ex.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
