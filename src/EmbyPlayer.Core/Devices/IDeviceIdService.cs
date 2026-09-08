namespace EmbyPlayer.Core.Devices;

public interface IDeviceIdService
{
    Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken);
}
