using AFKNotifier.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AFKNotifier.Services;

public sealed class ProcessAudioCaptureService
{
    private WasapiRecorder? _recorder;
    private FloatStereoToPcm16MonoResampler? _resampler;

    public event Action<byte[]>? AudioDataAvailable;

    public async Task StartAsync(int processId)
    {
        await StopAsync();

        _resampler = new FloatStereoToPcm16MonoResampler();
        _recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithBufferLength(50)
            .BuildAsync();

        _recorder.DataAvailable += (buffer, _, _, _) =>
        {
            var converted = _resampler?.Convert(buffer);
            if (converted is { Length: > 0 })
            {
                AudioDataAvailable?.Invoke(converted);
            }
        };

        _recorder.StartRecording();
    }

    public async Task StopAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        _resampler = null;

        if (recorder is null)
        {
            return;
        }

        try
        {
            recorder.StopRecording();
        }
        finally
        {
            await recorder.DisposeAsync();
        }
    }
}
