using System.Runtime.InteropServices;
using Whisper.net;
using Whisper.net.Ggml;

namespace AFKNotifier.Services;

public sealed record SpeechObservation(
    string Text,
    float Confidence,
    float NoSpeechProbability,
    bool MatchesTrigger,
    int TriggerConfirmations,
    bool IsSpeech);

public sealed class SpeechRecognizerService : IDisposable
{
    private const int SampleRate = 16000;
    private const int WindowSamples = SampleRate * 5 / 2;
    private const int HopSamples = SampleRate;
    private const int MaxBufferedSamples = SampleRate * 5;
    private const int TriggerConfirmationsRequired = 2;
    private const double MinimumAnalysisDbFs = -55;
    private const float MinimumTranscriptProbability = 0.18f;
    private const float MinimumTriggerProbability = 0.30f;
    private const float MaximumNoSpeechProbability = 0.65f;
    private const long MinimumModelBytes = 50_000_000;

    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AFK Notifier",
        "Models",
        "ggml-tiny.en.bin");

    private readonly object _bufferLock = new();
    private readonly List<float> _audioBuffer = [];
    private readonly string _triggerPhrase;

    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private int _samplesSinceLastAnalysis;
    private int _consecutiveTriggerMatches;
    private int _triggerRaised;
    private bool _disposed;

    public SpeechRecognizerService(string triggerPhrase)
    {
        _triggerPhrase = triggerPhrase;
    }

    public event Action<string, float>? TriggerDetected;
    public event Action<SpeechObservation>? SpeechObserved;
    public event Action<double>? InputLevelUpdated;

    public bool RequiresModelDownload => !IsModelValid();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_processingTask is not null)
        {
            return;
        }

        var modelPath = await EnsureModelAsync(cancellationToken);

        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .WithProbabilities()
            .WithNoSpeechThreshold(0.60f)
            .Build();

        _processingCancellation = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessingLoopAsync(_processingCancellation.Token));
    }

    public void FeedAudio(ReadOnlySpan<byte> data)
    {
        if (_processingTask is null || data.Length < sizeof(short))
        {
            return;
        }

        var byteCount = data.Length - (data.Length % sizeof(short));
        var pcm = MemoryMarshal.Cast<byte, short>(data[..byteCount]);
        if (pcm.Length == 0)
        {
            return;
        }

        InputLevelUpdated?.Invoke(CalculateDbFs(pcm));

        var samples = new float[pcm.Length];
        for (var index = 0; index < pcm.Length; index++)
        {
            samples[index] = pcm[index] / 32768f;
        }

        lock (_bufferLock)
        {
            _audioBuffer.AddRange(samples);
            _samplesSinceLastAnalysis += samples.Length;

            if (_audioBuffer.Count > MaxBufferedSamples)
            {
                _audioBuffer.RemoveRange(0, _audioBuffer.Count - MaxBufferedSamples);
            }
        }
    }

    public async Task StopAsync()
    {
        var cancellation = _processingCancellation;
        var processingTask = _processingTask;

        _processingCancellation = null;
        _processingTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        if (processingTask is not null)
        {
            try
            {
                await processingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation?.Dispose();

        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;

        lock (_bufferLock)
        {
            _audioBuffer.Clear();
            _samplesSinceLastAnalysis = 0;
        }

        _consecutiveTriggerMatches = 0;
        Interlocked.Exchange(ref _triggerRaised, 0);
    }

    private async Task ProcessingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken);

            float[]? window = null;

            lock (_bufferLock)
            {
                if (_audioBuffer.Count >= WindowSamples && _samplesSinceLastAnalysis >= HopSamples)
                {
                    window = _audioBuffer
                        .GetRange(_audioBuffer.Count - WindowSamples, WindowSamples)
                        .ToArray();
                    _samplesSinceLastAnalysis = 0;
                }
            }

            if (window is null)
            {
                continue;
            }

            var windowDbFs = CalculateDbFs(window);
            if (windowDbFs < MinimumAnalysisDbFs)
            {
                _consecutiveTriggerMatches = 0;
                SpeechObserved?.Invoke(new SpeechObservation(
                    string.Empty,
                    0,
                    1,
                    false,
                    0,
                    false));
                continue;
            }

            await AnalyzeWindowAsync(window, cancellationToken);
        }
    }

    private async Task AnalyzeWindowAsync(float[] samples, CancellationToken cancellationToken)
    {
        var processor = _processor;
        if (processor is null)
        {
            return;
        }

        var segments = new List<SegmentData>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                segments.Add(segment);
            }
        }

        var usableSegments = segments
            .Where(segment =>
                segment.Probability >= MinimumTranscriptProbability &&
                segment.NoSpeechProbability <= MaximumNoSpeechProbability)
            .ToArray();

        if (usableSegments.Length == 0)
        {
            _consecutiveTriggerMatches = 0;
            var noSpeechProbability = segments.Count == 0
                ? 1f
                : segments.Average(segment => segment.NoSpeechProbability);

            SpeechObserved?.Invoke(new SpeechObservation(
                string.Empty,
                0,
                noSpeechProbability,
                false,
                0,
                false));
            return;
        }

        var text = string.Join(' ', usableSegments.Select(segment => segment.Text.Trim())).Trim();
        var confidence = usableSegments.Average(segment => segment.Probability);
        var noSpeech = usableSegments.Average(segment => segment.NoSpeechProbability);
        var matchesTrigger = TriggerDetector.Matches(text, _triggerPhrase) &&
            confidence >= MinimumTriggerProbability &&
            noSpeech <= MaximumNoSpeechProbability;

        if (matchesTrigger)
        {
            _consecutiveTriggerMatches++;
        }
        else
        {
            _consecutiveTriggerMatches = 0;
        }

        SpeechObserved?.Invoke(new SpeechObservation(
            text,
            confidence,
            noSpeech,
            matchesTrigger,
            _consecutiveTriggerMatches,
            true));

        if (matchesTrigger &&
            _consecutiveTriggerMatches >= TriggerConfirmationsRequired &&
            Interlocked.Exchange(ref _triggerRaised, 1) == 0)
        {
            TriggerDetected?.Invoke(text, confidence);
        }
    }

    private static async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        if (IsModelValid())
        {
            return ModelPath;
        }

        var directory = Path.GetDirectoryName(ModelPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = ModelPath + ".download";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        try
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.TinyEn);
            await using (var fileStream = File.Create(temporaryPath))
            {
                await modelStream.CopyToAsync(fileStream, cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length < MinimumModelBytes)
            {
                throw new InvalidDataException("The downloaded Whisper model is incomplete.");
            }

            File.Move(temporaryPath, ModelPath, true);
            return ModelPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static bool IsModelValid()
    {
        return File.Exists(ModelPath) && new FileInfo(ModelPath).Length >= MinimumModelBytes;
    }

    private static double CalculateDbFs(ReadOnlySpan<short> samples)
    {
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

        return RmsToDbFs(Math.Sqrt(sumSquares / samples.Length));
    }

    private static double CalculateDbFs(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return -120;
        }

        double sumSquares = 0;
        foreach (var sample in samples)
        {
            sumSquares += sample * sample;
        }

        return RmsToDbFs(Math.Sqrt(sumSquares / samples.Length));
    }

    private static double RmsToDbFs(double rms)
    {
        if (rms <= 0.000001)
        {
            return -120;
        }

        return Math.Clamp(20.0 * Math.Log10(rms), -120, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
