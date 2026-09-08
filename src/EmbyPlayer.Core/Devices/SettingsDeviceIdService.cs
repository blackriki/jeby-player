using EmbyPlayer.Core.Settings;

namespace EmbyPlayer.Core.Devices;

public sealed class SettingsDeviceIdService : IDeviceIdService
{
    private readonly IAppSettingsService appSettingsService;
    private readonly SemaphoreSlim deviceIdGate = new(1, 1);

    public SettingsDeviceIdService(IAppSettingsService appSettingsService)
    {
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
    }

    public async Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
    {
        await deviceIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingDeviceId = await appSettingsService
                .GetDeviceIdAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existingDeviceId))
            {
                return existingDeviceId;
            }

            var deviceId = Guid.NewGuid().ToString("N");
            await appSettingsService
                .SaveDeviceIdAsync(deviceId, cancellationToken)
                .ConfigureAwait(false);

            return deviceId;
        }
        finally
        {
            deviceIdGate.Release();
        }
    }
}
