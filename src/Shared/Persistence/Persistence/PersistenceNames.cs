using System.Text.RegularExpressions;
namespace DataBridge.Persistence;
public static class PersistenceNames
{
    public static string SnakeCase(string value) => Regex.Replace(Regex.Replace(value, "([A-Z]+)([A-Z][a-z])", "$1_$2"), "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
