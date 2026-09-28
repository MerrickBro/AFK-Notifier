using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using AFKNotifier.Audio;

namespace AFKNotifier.Services;

public sealed record SpeechObservation(
    string Text,
    float Confidence,
    string GrammarName,
    bool IsFinal,
    bool IsRejected);

public sealed class SpeechRecognizerService : IDisposable
{
    private const string TriggerGrammarName = "Trigger";

    private readonly string _triggerPhrase;
    private readonly float _minimumConfidence;
    private readonly BlockingAudioStream _audioStream = new();
    private SpeechRecognitionEngine? _recognizer;
    private double _latestInputDbFs = -120;
    private bool _disposed;

    public SpeechRecognizerService(string triggerPhrase, float minimumConfidence = 0.68f)
    {
        _triggerPhrase = triggerPhrase;
        _minimumConfidence = minimumConfidence;
    }

    public event Action<string, float>? TriggerDetected;
    public event Action<SpeechObservation>? SpeechObserved;
    public event Action<double>? InputLevelUpdated;

    public void Start()
    {
        if (_recognizer is not null)
        {
            return;
        }

        var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var recognizerInfo = installedRecognizers
            .FirstOrDefault(info => info.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? installedRecognizers.FirstOrDefault()
            ?? throw new InvalidOperationException("No Windows speech recognizer is installed.");

        var recognizer = new SpeechRecognitionEngine(recognizerInfo)
        {
            MaxAlternates = 3
        };

        var choices = new Choices();
        choices.Add(_triggerPhrase);

        var spelledVariant = GetSpelledAcronymVariant(_triggerPhrase);
        if (!string.IsNullOrWhiteSpace(spelledVariant) &&
            !spelledVariant.Equals(_triggerPhrase, StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(spelledVariant);
        }

        var grammarBuilder = new GrammarBuilder
        {
            Culture = recognizerInfo.Culture
        };
        grammarBuilder.Append(choices);

        var grammar = new Grammar(grammarBuilder)
        {
            Name = TriggerGrammarName,
            Priority = 10,
            Weight = 1.0f
        };

        recognizer.LoadGrammar(grammar);
        recognizer.SpeechRecognized += OnSpeechRecognized;
        recognizer.SpeechRecognitionRejected += OnSpeechRejected;
        recognizer.SetInputToAudioStream(
            _audioStream,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        _recognizer = recognizer;
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void FeedAudio(ReadOnlySpan<byte> data)
    {
        _latestInputDbFs = CalculateDbFs(data);
        InputLevelUpdated?.Invoke(_latestInputDbFs);
        _audioStream.Enqueue(data);
    }

    public void Stop()
    {
        var recognizer = _recognizer;
        _recognizer = null;

        if (recognizer is null)
        {
            return;
        }

        _audioStream.Complete();

        try
        {
            recognizer.RecognizeAsyncCancel();
        }
        catch
        {
        }

        recognizer.SpeechRecognized -= OnSpeechRecognized;
        recognizer.SpeechRecognitionRejected -= OnSpeechRejected;
        recognizer.Dispose();
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        var result = eventArgs.Result;

        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            BuildSourceLabel(),
            true,
            false));

        if (result.Confidence < _minimumConfidence)
        {
            return;
        }

        if (TriggerDetector.Matches(result.Text, _triggerPhrase))
        {
            TriggerDetected?.Invoke(result.Text, result.Confidence);
        }
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        SpeechObserved?.Invoke(new SpeechObservation(
            string.Empty,
            result?.Confidence ?? 0,
            BuildSourceLabel(),
            true,
            true));
    }

    private string BuildSourceLabel()
    {
        var levelState = _latestInputDbFs switch
        {
            > -3 => "HOT",
            < -48 => "LOW",
            _ => "OK"
        };

        return $"PHRASE SPOTTER // INPUT {_latestInputDbFs:0} DBFS {levelState}";
    }

    private static string? GetSpelledAcronymVariant(string phrase)
    {
        var compact = new string(phrase.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length is < 2 or > 6 || !compact.All(char.IsLetter))
        {
            return null;
        }

        var visibleLetters = phrase.Where(char.IsLetter).ToArray();
        var looksLikeAcronym = visibleLetters.All(char.IsUpper) || phrase.Any(character => char.IsWhiteSpace(character) || character is '.' or '-' or '_');
        if (!looksLikeAcronym)
        {
            return null;
        }

        return string.Join(' ', compact.ToUpperInvariant().ToCharArray());
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _audioStream.Dispose();
    }
}
