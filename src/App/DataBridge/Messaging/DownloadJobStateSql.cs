using System.Text;
using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.Messaging;

internal static class DownloadJobStateSql
{
    private static readonly string[] ActiveStates = new[]
    {
        DownloadJobState.Queued,
        DownloadJobState.MetadataPending,
        DownloadJobState.MetadataResolved,
        DownloadJobState.DownloadPending,
        DownloadJobState.DownloadedTemp,
        DownloadJobState.UploadPending,
        DownloadJobState.Uploaded,
        DownloadJobState.CommitPending,
        DownloadJobState.Compensating,
        DownloadJobState.FailedTransient
    }.Select(ToPostgresName).ToArray();

    public static void AddActiveStatesParameter(NpgsqlCommand command)
        => command.Parameters.Add("active_download_job_states", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = ActiveStates;

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
