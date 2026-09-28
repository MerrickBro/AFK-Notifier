using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace AFKNotifier.Services;

public sealed record SpeechObservation(
    string Text,
    float Confidence,
    bool MatchesTrigger,
    bool TriggerVerified,
    bool IsFinal,
    bool IsSpeech);

public sealed class SpeechRecognizerService : IDisposable
{
    private const float StrongFinalConfidence = 0.55f;

    private static readonly TimeSpan PartialVerificationWindow = TimeSpan.FromMilliseconds(900);
    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AFK Notifier",
        "Models",
        "vosk-model-small-en-us-0.15");

    private readonly string _triggerPhrase;
    private readonly Channel<byte[]> _audioQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(12)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly SemaphoreSlim _stopGate = new(1, 1);

    private CancellationTokenSource? _runCancellation;
    private Process? _worker;
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _errorReaderTask;
    private TaskCompletionSource<bool>? _ready;
    private string _lastTriggerText = string.Empty;
    private string _lastPublishedText = string.Empty;
    private DateTime _lastTriggerUtc = DateTime.MinValue;
    private int _triggerEvidence;
    private int _triggerRaised;
    private int _stopping;
    private bool _disposed;

    public SpeechRecognizerService(string triggerPhrase)
    {
        _triggerPhrase = triggerPhrase;
    }

    public event Action<string, float>? TriggerDetected;
    public event Action<SpeechObservation>? SpeechObserved;
    public event Action<double>? InputLevelUpdated;
    public event Action<string>? RecognitionFaulted;

    public bool RequiresModelDownload => !IsModelValid();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_worker is not null)
        {
            return;
        }

        var workerPath = Path.Combine(
            AppContext.BaseDirectory,
            "RecognizerWorker",
            "AFKNotifier.RecognizerWorker.exe");

        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The speech recognition worker is missing.", workerPath);
        }

        var startInfo = new ProcessStartInfo(workerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var worker = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!worker.Start())
        {
            worker.Dispose();
            throw new InvalidOperationException("The speech recognition worker could not be started.");
        }

        _worker = worker;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _stopping, 0);

        worker.Exited += OnWorkerExited;

        var token = _runCancellation.Token;
        _writerTask = Task.Run(() => WriteAudioAsync(worker, token), token);
        _readerTask = Task.Run(() => ReadOutputAsync(worker, token), token);
        _errorReaderTask = Task.Run(() => DrainErrorsAsync(worker, token), token);

        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public void FeedAudio(ReadOnlySpan<byte> data)
    {
        if (_disposed || Volatile.Read(ref _stopping) != 0 || data.Length < sizeof(short))
        {
            return;
        }

        var byteCount = data.Length - (data.Length % sizeof(short));
        var samples = MemoryMarshal.Cast<byte, short>(data[..byteCount]);
        if (samples.Length == 0)
        {
            return;
        }

        InputLevelUpdated?.Invoke(CalculateDbFs(samples));
        _audioQueue.Writer.TryWrite(data[..byteCount].ToArray());
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync();

        try
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }

            var worker = _worker;
            _worker = null;

            _audioQueue.Writer.TryComplete();

            try
            {
                if (_writerTask is not null)
                {
                    await _writerTask.WaitAsync(TimeSpan.FromSeconds(1));
                }
            }
            catch
            {
            }

            if (worker is not null)
            {
                try
                {
                    worker.StandardInput.Close();
                }
                catch
                {
                }

                try
                {
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch
                {
                    try
                    {
                        if (!worker.HasExited)
                        {
                            worker.Kill(true);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                _runCancellation?.Cancel();
            }
            catch
            {
            }

            await IgnoreTaskFailureAsync(_readerTask);
            await IgnoreTaskFailureAsync(_errorReaderTask);
            await IgnoreTaskFailureAsync(_writerTask);

            if (worker is not null)
            {
                worker.Exited -= OnWorkerExited;
                worker.Dispose();
            }

            _runCancellation?.Dispose();
            _runCancellation = null;
            _writerTask = null;
            _readerTask = null;
            _errorReaderTask = null;
            _ready = null;
            _lastTriggerText = string.Empty;
            _lastPublishedText = string.Empty;
            _lastTriggerUtc = DateTime.MinValue;
            _triggerEvidence = 0;
            Interlocked.Exchange(ref _triggerRaised, 0);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task WriteAudioAsync(Process worker, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audio in _audioQueue.Reader.ReadAllAsync(cancellationToken))
            {
                await worker.StandardInput.BaseStream.WriteAsync(audio.AsMemory(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException) when (Volatile.Read(ref _stopping) != 0 || worker.HasExited)
        {
        }
        catch (Exception exception)
        {
            ReportWorkerFault($"AUDIO PIPE FAILED: {exception.Message}");
        }
        finally
        {
            try
            {
                worker.StandardInput.Close();
            }
            catch
            {
            }
        }
    }

    private async Task ReadOutputAsync(Process worker, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                HandleWorkerMessage(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportWorkerFault($"RECOGNIZER OUTPUT FAILED: {exception.Message}");
        }
    }

    private async Task DrainErrorsAsync(Process worker, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await worker.StandardError.ReadLineAsync(cancellationToken) is not null)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void HandleWorkerMessage(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            if (type == "ready")
            {
                _ready?.TrySetResult(true);
                return;
            }

            if (type == "error")
            {
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "Unknown speech worker error.";
                _ready?.TrySetException(new InvalidOperationException(message));
                ReportWorkerFault(message ?? "Unknown speech worker error.");
                return;
            }

            if (type is not ("partial" or "final"))
            {
                return;
            }

            var text = root.TryGetProperty("text", out var textElement)
                ? textElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            var confidence = root.TryGetProperty("confidence", out var confidenceElement) &&
                confidenceElement.TryGetSingle(out var parsedConfidence)
                    ? parsedConfidence
                    : 0f;

            HandleRecognition(text, confidence, type == "final");
        }
        catch (JsonException)
        {
        }
    }

    private void HandleRecognition(string text, float confidence, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var matchesTrigger = TriggerDetector.Matches(text, _triggerPhrase);
        var verified = false;

        if (matchesTrigger)
        {
            var now = DateTime.UtcNow;
            if (text.Equals(_lastTriggerText, StringComparison.OrdinalIgnoreCase) &&
                now - _lastTriggerUtc <= PartialVerificationWindow)
            {
                _triggerEvidence++;
            }
            else
            {
                _triggerEvidence = 1;
            }

            _lastTriggerText = text;
            _lastTriggerUtc = now;

            verified = (isFinal && confidence >= StrongFinalConfidence) || _triggerEvidence >= 2;
        }
        else if (DateTime.UtcNow - _lastTriggerUtc > PartialVerificationWindow)
        {
            _triggerEvidence = 0;
            _lastTriggerText = string.Empty;
        }

        var shouldPublish = isFinal ||
            matchesTrigger ||
            !text.Equals(_lastPublishedText, StringComparison.OrdinalIgnoreCase);

        if (shouldPublish)
        {
            _lastPublishedText = text;
            SpeechObserved?.Invoke(new SpeechObservation(
                text,
                confidence,
                matchesTrigger,
                verified,
                isFinal,
                true));
        }

        if (verified && Interlocked.Exchange(ref _triggerRaised, 1) == 0)
        {
            TriggerDetected?.Invoke(text, confidence);
        }
    }

    private void OnWorkerExited(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        var exitCode = sender is Process process && process.HasExited ? process.ExitCode : -1;
        var message = $"Speech worker exited unexpectedly (code {exitCode}).";
        _ready?.TrySetException(new InvalidOperationException(message));
        ReportWorkerFault(message);
    }

    private void ReportWorkerFault(string message)
    {
        if (Volatile.Read(ref _stopping) == 0)
        {
            RecognitionFaulted?.Invoke(message);
        }
    }

    private static bool IsModelValid()
    {
        return File.Exists(Path.Combine(ModelPath, "am", "final.mdl")) &&
            File.Exists(Path.Combine(ModelPath, "conf", "model.conf"));
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

        var rms = Math.Sqrt(sumSquares / samples.Length);
        if (rms <= 0.000001)
        {
            return -120;
        }

        return Math.Clamp(20.0 * Math.Log10(rms), -120, 0);
    }

    private static async Task IgnoreTaskFailureAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch
        {
        }
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
