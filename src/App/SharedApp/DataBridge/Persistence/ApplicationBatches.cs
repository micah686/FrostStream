using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Persistence;

public static class ApplicationBatches
{
    // Leave room for fixed predicates and update values even on SQLite builds with a 999-variable limit.
    public const int MembershipBatchSize = 400;
    public const int WriteBatchSize = 500;

    public static async Task<List<T>> WithIdsAsync<T>(this IQueryable<T> query, IEnumerable<Guid> ids,
        Expression<Func<T, Guid>> key, CancellationToken ct) where T : class
    {
        var rows = new List<T>();
        foreach (var chunk in ids.Distinct().Chunk(MembershipBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var predicate = Expression.Lambda<Func<T, bool>>(
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)],
                    Expression.Constant(chunk), key.Body), key.Parameters);
            rows.AddRange(await query.Where(predicate).ToListAsync(ct));
        }
        return rows;
    }
}
