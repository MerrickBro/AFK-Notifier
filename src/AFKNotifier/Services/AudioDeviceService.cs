using AFKNotifier.Models;
using NAudio.CoreAudioApi;

namespace AFKNotifier.Services;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceOption> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDeviceOption(device.ID, device.FriendlyName))
            .OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
