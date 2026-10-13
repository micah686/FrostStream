using Microsoft.EntityFrameworkCore;
namespace DataBridge.Persistence;
public static class ApplicationDatabaseErrors
{
    public static bool IsUniqueViolation(Exception exception) => exception is DbUpdateException { InnerException: { } inner }
        ? IsUniqueViolation(inner) : PersistenceDialects.IsUniqueViolation(exception);
}
