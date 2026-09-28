using System.Runtime.InteropServices;

namespace AFKNotifier.Audio;

public sealed class FloatStereoToPcm16MonoResampler
{
    private const int InputSampleRate = 44100;
    private const int OutputSampleRate = 16000;
    private const int BytesPerInputFrame = sizeof(float) * 2;
    private readonly double _inputFramesPerOutputFrame = (double)InputSampleRate / OutputSampleRate;
    private double _nextOutputPosition;
    private long _inputFramePosition;
    private float _previousSample;
    private bool _hasPreviousSample;

    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        var frameCount = input.Length / BytesPerInputFrame;
        if (frameCount == 0)
        {
            return Array.Empty<byte>();
        }

        var samples = MemoryMarshal.Cast<byte, float>(input[..(frameCount * BytesPerInputFrame)]);
        var estimatedOutputFrames = Math.Max(1, (int)Math.Ceiling(frameCount * (double)OutputSampleRate / InputSampleRate) + 2);
        var output = new byte[estimatedOutputFrames * sizeof(short)];
        var outputOffset = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var mono = (samples[frame * 2] + samples[(frame * 2) + 1]) * 0.5f;
            var currentPosition = _inputFramePosition++;

            if (!_hasPreviousSample)
            {
                _previousSample = mono;
                _hasPreviousSample = true;
                continue;
            }

            var segmentStart = currentPosition - 1;
            while (_nextOutputPosition <= currentPosition)
            {
                var fraction = _nextOutputPosition - segmentStart;
                if (fraction < 0)
                {
                    _nextOutputPosition += _inputFramesPerOutputFrame;
                    continue;
                }

                var value = _previousSample + ((mono - _previousSample) * (float)fraction);
                value = Math.Clamp(value, -1f, 1f);
                var pcm = value >= 0
                    ? (short)Math.Round(value * short.MaxValue)
                    : (short)Math.Round(value * 32768f);

                if (outputOffset + 2 > output.Length)
                {
                    Array.Resize(ref output, output.Length + 256);
                }

                output[outputOffset++] = (byte)(pcm & 0xFF);
                output[outputOffset++] = (byte)((pcm >> 8) & 0xFF);
                _nextOutputPosition += _inputFramesPerOutputFrame;
            }

            _previousSample = mono;
        }

        if (outputOffset == output.Length)
        {
            return output;
        }

        Array.Resize(ref output, outputOffset);
        return output;
    }
}
