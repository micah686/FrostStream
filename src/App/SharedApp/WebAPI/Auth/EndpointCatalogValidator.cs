using System.Reflection;

namespace WebAPI.Auth;

/// <summary>
/// Startup drift guard. Reflects over every controller action carrying an <see cref="EndpointAttribute"/>
/// and asserts a 1:1 match with <see cref="EndpointCatalog"/>: no action declares an id missing from
/// the registry, no registry id is left unrouted, and no id is declared twice. A mismatch is the
/// Finding-1 class of bug, so it fails fast at startup rather than shipping a route nobody can call.
/// </summary>
public static class EndpointCatalogValidator
{
    public static void Validate(Assembly assembly)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attribute = method.GetCustomAttribute<EndpointAttribute>(inherit: false);
                if (attribute is null)
                {
                    continue;
                }

                var location = $"{type.Name}.{method.Name}";
                if (declared.TryGetValue(attribute.EndpointId, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Endpoint id '{attribute.EndpointId}' is declared by both {existing} and {location}. Ids must be unique.");
                }

                declared[attribute.EndpointId] = location;
            }
        }

        var declaredIds = declared.Keys.ToHashSet(StringComparer.Ordinal);

        var unknown = declaredIds.Where(id => !EndpointCatalog.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"[Endpoint] declares id(s) not present in EndpointCatalog: {string.Join(", ", unknown)}.");
        }

        var unrouted = EndpointCatalog.Ids.Where(id => !declaredIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (unrouted.Length > 0)
        {
            throw new InvalidOperationException(
                $"EndpointCatalog lists id(s) with no [Endpoint] route behind them: {string.Join(", ", unrouted)}.");
        }
    }
}
