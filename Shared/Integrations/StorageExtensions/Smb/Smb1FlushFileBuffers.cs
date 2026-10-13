using System.Reflection;
using System.Runtime.ExceptionServices;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.SMB1;

namespace StorageExtensions.Smb;

/// <summary>
/// Sends SMB_COM_FLUSH for SMB1. SMBLibrary models the request but leaves
/// <see cref="SMB1FileStore.FlushFileBuffers"/> unimplemented, and keeps the send/wait
/// primitives and tree id internal. This adapter bridges that one missing client operation.
/// </summary>
internal static class Smb1FlushFileBuffers
{
    private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo TreeIdField = typeof(SMB1FileStore).GetField("m_treeID", NonPublicInstance)
        ?? throw new MissingFieldException(typeof(SMB1FileStore).FullName, "m_treeID");

    private static readonly MethodInfo TrySendMessageMethod = typeof(SMB1Client).GetMethod(
        "TrySendMessage",
        NonPublicInstance,
        binder: null,
        [typeof(SMB1Command), typeof(ushort)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(SMB1Client).FullName, "TrySendMessage(SMB1Command, ushort)");

    private static readonly MethodInfo WaitForMessageMethod = typeof(SMB1Client).GetMethod(
        "WaitForMessage",
        NonPublicInstance,
        binder: null,
        [typeof(CommandName), typeof(bool).MakeByRefType()],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(SMB1Client).FullName, "WaitForMessage(CommandName, out bool)");

    public static NTStatus Flush(SMB1Client client, SMB1FileStore tree, object handle)
    {
        if (!client.IsConnected)
        {
            throw new IOException("The SMB connection was lost while flushing a file.");
        }

        var request = CreateRequest(handle);
        Invoke(TrySendMessageMethod, client, [request, GetTreeId(tree)]);

        object?[] waitArguments = [CommandName.SMB_COM_FLUSH, false];
        var reply = (SMB1Message?)Invoke(WaitForMessageMethod, client, waitArguments);
        if (reply is not null)
        {
            return reply.Header.Status;
        }

        return waitArguments[1] is true
            ? NTStatus.STATUS_INVALID_SMB
            : NTStatus.STATUS_IO_TIMEOUT;
    }

    internal static FlushRequest CreateRequest(object handle)
    {
        if (handle is not ushort fileId)
        {
            throw new ArgumentException("An SMB1 file handle must be a 16-bit file identifier.", nameof(handle));
        }

        return new FlushRequest { FID = fileId };
    }

    internal static ushort GetTreeId(SMB1FileStore tree)
        => (ushort)(TreeIdField.GetValue(tree)
            ?? throw new InvalidOperationException("SMBLibrary returned no SMB1 tree id."));

    private static object? Invoke(MethodInfo method, object target, object?[]? arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
