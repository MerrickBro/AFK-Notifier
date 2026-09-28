using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AFKNotifier.Services;

public sealed class NotifierService
{
    public async Task RunAlertAsync(
        string deviceId,
        TimeSpan interval,
        double frequency,
        double volume,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PlayToneAsync(deviceId, frequency, volume, TimeSpan.FromMilliseconds(150), cancellationToken);
            await Task.Delay(interval, cancellationToken);
        }
    }

    public async Task PlayConfirmationAsync(string deviceId, double frequency, double volume)
    {
        await PlayToneAsync(deviceId, frequency, volume, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.Delay(65);
        await PlayToneAsync(deviceId, frequency * 1.35, volume, TimeSpan.FromMilliseconds(160), CancellationToken.None);
    }

    private static async Task PlayToneAsync(
        string deviceId,
        double frequency,
        double volume,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);

        var signal = new SignalGenerator(44100, 1)
        {
            Frequency = Math.Clamp(frequency, 50, 5000),
            Gain = Math.Clamp(volume, 0, 1),
            Type = SignalGeneratorType.Sin
        };

        var tone = new OffsetSampleProvider(signal)
        {
            Take = duration
        };

        await using var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .Build();

        player.Init(tone.ToWaveProvider());
        player.Play();

        try
        {
            while (player.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            player.Stop();
            throw;
        }
    }
}
