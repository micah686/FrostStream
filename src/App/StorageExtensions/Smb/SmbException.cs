using SMBLibrary;

namespace StorageExtensions.Smb;

/// <summary>
/// An SMB operation that returned a failing <see cref="NTStatus"/>.
/// </summary>
public sealed class SmbException(string message, NTStatus status) : IOException($"{message} (NTStatus: {status})")
{
    /// <summary>The status code the server returned.</summary>
    public NTStatus Status { get; } = status;
}

internal static class NtStatusExtensions
{
    /// <summary>
    /// Statuses that mean "the path is not there", which the store surfaces as
    /// <see langword="false"/> or <see langword="null"/> rather than as an error.
    /// </summary>
    public static bool IsNotFound(this NTStatus status)
        => status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
            or NTStatus.STATUS_NO_SUCH_FILE
            or NTStatus.STATUS_NOT_FOUND
            or NTStatus.STATUS_NOT_A_DIRECTORY;

    public static void EnsureSuccess(this NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new SmbException($"SMB {operation} failed.", status);
        }
    }
}
