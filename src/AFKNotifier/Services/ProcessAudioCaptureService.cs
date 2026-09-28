using System.Runtime.InteropServices;
using AFKNotifier.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AFKNotifier.Services;

public sealed class ProcessAudioCaptureService
{
    private WasapiRecorder? _recorder;
    private FloatStereoToPcm16MonoResampler? _resampler;

    public event Action<byte[]>? AudioDataAvailable;
    public event Action<double>? AudioLevelAvailable;

    public async Task StartAsync(int processId)
    {
        await StopAsync();

        _resampler = new FloatStereoToPcm16MonoResampler();
        _recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
            .WithBufferLength(50)
            .BuildAsync();

        _recorder.DataAvailable += (buffer, _, _, _) =>
        {
            var converted = _resampler?.Convert(buffer);
            if (converted is not { Length: > 0 })
            {
                return;
            }

            AudioLevelAvailable?.Invoke(CalculateDbFs(converted));
            AudioDataAvailable?.Invoke(converted);
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

    private static double CalculateDbFs(ReadOnlySpan<byte> pcm16)
    {
        var byteCount = pcm16.Length - (pcm16.Length % sizeof(short));
        if (byteCount <= 0)
        {
            return -120;
        }

        var samples = MemoryMarshal.Cast<byte, short>(pcm16[..byteCount]);
        if (samples.Length == 0)
        {
            return -120;
        }

        double sumSquares = 0;
        foreach (var sample in samples)
        {
            var normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);
        if (rms <= 0.000001)
        {
            return -120;
        }

        return Math.Clamp(20.0 * Math.Log10(rms), -120, 0);
    }
}
