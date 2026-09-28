using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using AFKNotifier.Audio;

namespace AFKNotifier.Services;

public sealed class SpeechRecognizerService : IDisposable
{
    private readonly string _triggerPhrase;
    private readonly float _minimumConfidence;
    private readonly BlockingAudioStream _audioStream = new();
    private SpeechRecognitionEngine? _recognizer;
    private bool _disposed;

    public SpeechRecognizerService(string triggerPhrase, float minimumConfidence = 0.55f)
    {
        _triggerPhrase = triggerPhrase;
        _minimumConfidence = minimumConfidence;
    }

    public event Action<string, float>? TriggerDetected;

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

        var recognizer = new SpeechRecognitionEngine(recognizerInfo);
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

        recognizer.LoadGrammar(new Grammar(grammarBuilder));
        recognizer.SpeechRecognized += OnSpeechRecognized;
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

        recognizer.SpeechRecognized -= OnSpeechRecognized;
        recognizer.Dispose();
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        if (eventArgs.Result.Confidence < _minimumConfidence)
        {
            return;
        }

        if (TriggerDetector.Matches(eventArgs.Result.Text, _triggerPhrase))
        {
            TriggerDetected?.Invoke(eventArgs.Result.Text, eventArgs.Result.Confidence);
        }
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
