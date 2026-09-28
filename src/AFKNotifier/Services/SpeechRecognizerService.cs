using System.Globalization;
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
    private const string DictationGrammarName = "Dictation";

    private readonly string _triggerPhrase;
    private readonly float _minimumConfidence;
    private readonly BlockingAudioStream _triggerAudioStream = new();
    private readonly BlockingAudioStream _dictationAudioStream = new();
    private SpeechRecognitionEngine? _triggerRecognizer;
    private SpeechRecognitionEngine? _dictationRecognizer;
    private bool _disposed;

    public SpeechRecognizerService(string triggerPhrase, float minimumConfidence = 0.62f)
    {
        _triggerPhrase = triggerPhrase;
        _minimumConfidence = minimumConfidence;
    }

    public event Action<string, float>? TriggerDetected;
    public event Action<SpeechObservation>? SpeechObserved;

    public void Start()
    {
        if (_triggerRecognizer is not null || _dictationRecognizer is not null)
        {
            return;
        }

        var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var recognizerInfo = installedRecognizers
            .FirstOrDefault(info => info.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? installedRecognizers.FirstOrDefault()
            ?? throw new InvalidOperationException("No Windows speech recognizer is installed.");

        var triggerRecognizer = CreateTriggerRecognizer(recognizerInfo);
        var dictationRecognizer = CreateDictationRecognizer(recognizerInfo);

        _triggerRecognizer = triggerRecognizer;
        _dictationRecognizer = dictationRecognizer;

        triggerRecognizer.RecognizeAsync(RecognizeMode.Multiple);
        dictationRecognizer.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void FeedAudio(ReadOnlySpan<byte> data)
    {
        _triggerAudioStream.Enqueue(data);
        _dictationAudioStream.Enqueue(data);
    }

    public void Stop()
    {
        var triggerRecognizer = _triggerRecognizer;
        var dictationRecognizer = _dictationRecognizer;
        _triggerRecognizer = null;
        _dictationRecognizer = null;

        _triggerAudioStream.Complete();
        _dictationAudioStream.Complete();

        StopTriggerRecognizer(triggerRecognizer);
        StopDictationRecognizer(dictationRecognizer);
    }

    private SpeechRecognitionEngine CreateTriggerRecognizer(RecognizerInfo recognizerInfo)
    {
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
        recognizer.SpeechRecognized += OnTriggerSpeechRecognized;
        recognizer.SpeechRecognitionRejected += OnTriggerSpeechRejected;
        recognizer.SetInputToAudioStream(
            _triggerAudioStream,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        return recognizer;
    }

    private SpeechRecognitionEngine CreateDictationRecognizer(RecognizerInfo recognizerInfo)
    {
        var recognizer = new SpeechRecognitionEngine(recognizerInfo)
        {
            MaxAlternates = 5
        };

        var dictationGrammar = new DictationGrammar
        {
            Name = DictationGrammarName
        };

        recognizer.LoadGrammar(dictationGrammar);
        recognizer.SpeechHypothesized += OnDictationSpeechHypothesized;
        recognizer.SpeechRecognized += OnDictationSpeechRecognized;
        recognizer.SpeechRecognitionRejected += OnDictationSpeechRejected;
        recognizer.SetInputToAudioStream(
            _dictationAudioStream,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        return recognizer;
    }

    private void OnTriggerSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        var result = eventArgs.Result;

        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            TriggerGrammarName,
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

    private void OnTriggerSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
        {
            return;
        }

        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            TriggerGrammarName,
            true,
            true));
    }

    private void OnDictationSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            DictationGrammarName,
            false,
            false));
    }

    private void OnDictationSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            DictationGrammarName,
            true,
            false));
    }

    private void OnDictationSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        SpeechObserved?.Invoke(new SpeechObservation(
            result?.Text ?? string.Empty,
            result?.Confidence ?? 0,
            DictationGrammarName,
            true,
            true));
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

    private void StopTriggerRecognizer(SpeechRecognitionEngine? recognizer)
    {
        if (recognizer is null)
        {
            return;
        }

        try
        {
            recognizer.RecognizeAsyncCancel();
        }
        catch
        {
        }

        recognizer.SpeechRecognized -= OnTriggerSpeechRecognized;
        recognizer.SpeechRecognitionRejected -= OnTriggerSpeechRejected;
        recognizer.Dispose();
    }

    private void StopDictationRecognizer(SpeechRecognitionEngine? recognizer)
    {
        if (recognizer is null)
        {
            return;
        }

        try
        {
            recognizer.RecognizeAsyncCancel();
        }
        catch
        {
        }

        recognizer.SpeechHypothesized -= OnDictationSpeechHypothesized;
        recognizer.SpeechRecognized -= OnDictationSpeechRecognized;
        recognizer.SpeechRecognitionRejected -= OnDictationSpeechRejected;
        recognizer.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _triggerAudioStream.Dispose();
        _dictationAudioStream.Dispose();
    }
}
