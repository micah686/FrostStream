using System.Text;
using System.Data.Common;
using DataBridge.Persistence;

using Shared.Messaging;

namespace DataBridge.Messaging;

internal static class DownloadJobStateSql
{
    internal static readonly DownloadJobState[] ActiveJobStates =
    {
        DownloadJobState.Queued,
        DownloadJobState.MetadataPending,
        DownloadJobState.MetadataResolved,
        DownloadJobState.DownloadQueued,
        DownloadJobState.DownloadPending,
        DownloadJobState.DownloadedTemp,
        DownloadJobState.UploadPending,
        DownloadJobState.Uploaded,
        DownloadJobState.CommitPending,
        DownloadJobState.Compensating,
        DownloadJobState.FailedTransient,
        DownloadJobState.Cancelling
    };

    private static readonly string[] ActiveStates = ActiveJobStates.Select(ToPostgresName).ToArray();

    public static void AddActiveStatesParameter(DbCommand command)
        => command.Parameters.Add("active_download_job_states", ApplicationParameterType.TextArray).Value = ActiveStates;

    /// <summary>
    /// Renders an enum member as the snake_case label its PostgreSQL enum type uses, for raw SQL that
    /// compares against <c>column::text</c> rather than relying on Npgsql enum mappings.
    /// </summary>
    public static string ToPostgresLabel<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => ToSnakeCase(value.ToString());

    public static string[] ToPostgresLabels<TEnum>(params TEnum[] values)
        where TEnum : struct, Enum
        => values.Select(ToPostgresLabel).ToArray();

    private static string ToPostgresName(DownloadJobState state)
        => ToSnakeCase(state.ToString());

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (i > 0 && char.IsUpper(character))
                builder.Append('_');

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
