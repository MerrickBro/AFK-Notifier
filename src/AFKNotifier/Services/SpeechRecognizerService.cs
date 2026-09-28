using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Whisper.net;

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
    private const int WindowSamples = SampleRate * 7 / 4;
    private const int HopSamples = SampleRate / 2;
    private const int MaxBufferedSamples = SampleRate * 3;
    private const double MinimumAnalysisDbFs = -55;
    private const float MinimumTranscriptProbability = 0.10f;
    private const float MinimumTriggerProbability = 0.20f;
    private const float StrongTriggerProbability = 0.50f;
    private const float MaximumNoSpeechProbability = 0.70f;
    private const float StrongTriggerMaximumNoSpeechProbability = 0.40f;
    private const long MinimumModelBytes = 50_000_000;
    private const string ModelDownloadUrl = "https://huggingface.co/sandrohanea/whisper.net/resolve/v5/classic/ggml-tiny.en.bin";

    private static readonly TimeSpan TriggerEvidenceWindow = TimeSpan.FromSeconds(2.2);
    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AFK Notifier",
        "Models",
        "ggml-tiny.en.bin");

    private readonly object _bufferLock = new();
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<float> _audioBuffer = [];
    private readonly string _triggerPhrase;

    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;
    private WhisperFactory? _factory;
    private int _samplesSinceLastAnalysis;
    private int _triggerEvidence;
    private int _triggerRaised;
    private DateTime _lastTriggerEvidenceUtc = DateTime.MinValue;
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

        lock (_stateLock)
        {
            if (_processingTask is not null)
            {
                return;
            }
        }

        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var startupToken = startupCancellation.Token;

        var modelPath = await EnsureModelAsync(startupToken);
        startupToken.ThrowIfCancellationRequested();

        var factory = WhisperFactory.FromPath(modelPath);

        lock (_stateLock)
        {
            if (_lifetimeCancellation.IsCancellationRequested || _disposed)
            {
                factory.Dispose();
                throw new OperationCanceledException(startupToken);
            }

            _factory = factory;
            _processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _processingTask = Task.Run(() => ProcessingLoopAsync(_processingCancellation.Token));
        }
    }

    public void FeedAudio(ReadOnlySpan<byte> data)
    {
        if (_lifetimeCancellation.IsCancellationRequested || data.Length < sizeof(short))
        {
            return;
        }

        lock (_stateLock)
        {
            if (_processingTask is null)
            {
                return;
            }
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
        await _stopGate.WaitAsync();

        try
        {
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                _lifetimeCancellation.Cancel();
            }

            Task? processingTask;
            CancellationTokenSource? processingCancellation;

            lock (_stateLock)
            {
                processingTask = _processingTask;
                processingCancellation = _processingCancellation;
            }

            processingCancellation?.Cancel();

            if (processingTask is not null)
            {
                try
                {
                    await processingTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception) when (_lifetimeCancellation.IsCancellationRequested)
                {
                }
            }

            WhisperFactory? factory;

            lock (_stateLock)
            {
                _processingTask = null;
                _processingCancellation = null;
                factory = _factory;
                _factory = null;
            }

            processingCancellation?.Dispose();
            factory?.Dispose();

            lock (_bufferLock)
            {
                _audioBuffer.Clear();
                _samplesSinceLastAnalysis = 0;
            }

            _triggerEvidence = 0;
            _lastTriggerEvidenceUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _triggerRaised, 0);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task ProcessingLoopAsync(CancellationToken cancellationToken)
    {
        WhisperFactory? factory;
        lock (_stateLock)
        {
            factory = _factory;
        }

        if (factory is null)
        {
            return;
        }

        using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .WithProbabilities()
            .WithNoSpeechThreshold(MaximumNoSpeechProbability)
            .Build();

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken);

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
                ExpireOldTriggerEvidence();
                SpeechObserved?.Invoke(new SpeechObservation(
                    string.Empty,
                    0,
                    1,
                    false,
                    _triggerEvidence,
                    false));
                continue;
            }

            var transcriptParts = new List<string>();
            var probabilities = new List<float>();
            var noSpeechProbabilities = new List<float>();

            await foreach (var segment in processor.ProcessAsync(window, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(segment.Text) ||
                    segment.Probability < MinimumTranscriptProbability ||
                    segment.NoSpeechProbability > MaximumNoSpeechProbability)
                {
                    continue;
                }

                transcriptParts.Add(segment.Text.Trim());
                probabilities.Add(segment.Probability);
                noSpeechProbabilities.Add(segment.NoSpeechProbability);
            }

            if (transcriptParts.Count == 0)
            {
                ExpireOldTriggerEvidence();
                SpeechObserved?.Invoke(new SpeechObservation(
                    string.Empty,
                    0,
                    1,
                    false,
                    _triggerEvidence,
                    false));
                continue;
            }

            var text = string.Join(' ', transcriptParts).Trim();
            var confidence = probabilities.Average();
            var noSpeech = noSpeechProbabilities.Average();
            var matchesTrigger = TriggerDetector.Matches(text, _triggerPhrase) &&
                confidence >= MinimumTriggerProbability &&
                noSpeech <= MaximumNoSpeechProbability;

            var confirmed = false;

            if (matchesTrigger)
            {
                var now = DateTime.UtcNow;
                _triggerEvidence = now - _lastTriggerEvidenceUtc <= TriggerEvidenceWindow
                    ? _triggerEvidence + 1
                    : 1;
                _lastTriggerEvidenceUtc = now;

                var strongMatch = confidence >= StrongTriggerProbability &&
                    noSpeech <= StrongTriggerMaximumNoSpeechProbability;
                confirmed = strongMatch || _triggerEvidence >= 2;
            }
            else
            {
                ExpireOldTriggerEvidence();
            }

            SpeechObserved?.Invoke(new SpeechObservation(
                text,
                confidence,
                noSpeech,
                matchesTrigger,
                confirmed ? 2 : Math.Min(_triggerEvidence, 1),
                true));

            if (confirmed && Interlocked.Exchange(ref _triggerRaised, 1) == 0)
            {
                TriggerDetected?.Invoke(text, confidence);
            }
        }
    }

    private void ExpireOldTriggerEvidence()
    {
        if (_triggerEvidence > 0 && DateTime.UtcNow - _lastTriggerEvidenceUtc > TriggerEvidenceWindow)
        {
            _triggerEvidence = 0;
            _lastTriggerEvidenceUtc = DateTime.MinValue;
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
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(15)
            };
            using var response = await httpClient.GetAsync(
                ModelDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
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

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}
