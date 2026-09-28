using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AFKNotifier.Services;

public sealed class ProcessAudioCaptureService
{
    private WasapiRecorder? _recorder;

    public event Action<byte[]>? AudioDataAvailable;

    public async Task StartAsync(int processId)
    {
        await StopAsync();

        _recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(new WaveFormat(16000, 16, 1))
            .WithBufferLength(50)
            .BuildAsync();

        _recorder.DataAvailable += (buffer, _, _, _) =>
        {
            AudioDataAvailable?.Invoke(buffer.ToArray());
        };

        _recorder.StartRecording();
    }

    public async Task StopAsync()
    {
        var recorder = _recorder;
        _recorder = null;

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
