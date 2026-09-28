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
    private readonly BlockingAudioStream _audioStream = new();
    private SpeechRecognitionEngine? _recognizer;
    private bool _disposed;

    public SpeechRecognizerService(string triggerPhrase, float minimumConfidence = 0.68f)
    {
        _triggerPhrase = triggerPhrase;
        _minimumConfidence = minimumConfidence;
    }

    public event Action<string, float>? TriggerDetected;
    public event Action<SpeechObservation>? SpeechObserved;

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
            MaxAlternates = 5
        };

        var choices = new Choices(_triggerPhrase);
        if (TriggerDetector.Matches("AFK", _triggerPhrase))
        {
            choices.Add("A F K");
        }

        var grammarBuilder = new GrammarBuilder
        {
            Culture = recognizerInfo.Culture
        };
        grammarBuilder.Append(choices);

        var triggerGrammar = new Grammar(grammarBuilder)
        {
            Name = TriggerGrammarName,
            Priority = 10,
            Weight = 1.0f
        };

        var dictationGrammar = new DictationGrammar
        {
            Name = DictationGrammarName,
            Priority = 0,
            Weight = 0.70f
        };

        recognizer.LoadGrammar(triggerGrammar);
        recognizer.LoadGrammar(dictationGrammar);
        recognizer.SpeechHypothesized += OnSpeechHypothesized;
        recognizer.SpeechRecognized += OnSpeechRecognized;
        recognizer.SpeechRecognitionRejected += OnSpeechRecognitionRejected;
        recognizer.SetInputToAudioStream(
            _audioStream,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        _recognizer = recognizer;
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void FeedAudio(ReadOnlySpan<byte> data)
    {
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

        recognizer.SpeechHypothesized -= OnSpeechHypothesized;
        recognizer.SpeechRecognized -= OnSpeechRecognized;
        recognizer.SpeechRecognitionRejected -= OnSpeechRecognitionRejected;
        recognizer.Dispose();
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            result.Grammar?.Name ?? "Hypothesis",
            false,
            false));
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        var grammarName = result.Grammar?.Name ?? "Unknown";

        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            grammarName,
            true,
            false));

        if (!grammarName.Equals(TriggerGrammarName, StringComparison.Ordinal))
        {
            return;
        }

        if (result.Confidence < _minimumConfidence)
        {
            return;
        }

        if (TriggerDetector.Matches(result.Text, _triggerPhrase))
        {
            TriggerDetected?.Invoke(result.Text, result.Confidence);
        }
    }

    private void OnSpeechRecognitionRejected(object? sender, SpeechRecognitionRejectedEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        SpeechObserved?.Invoke(new SpeechObservation(
            result.Text,
            result.Confidence,
            result.Grammar?.Name ?? "Rejected",
            true,
            true));
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
