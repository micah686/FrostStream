namespace FrostStream.ApplicationContracts;

/// <summary>Updates the executable media tools managed by this application process.</summary>
public interface IMediaToolsUpdater
{
    Task UpdateNowAsync(CancellationToken cancellationToken);
}
