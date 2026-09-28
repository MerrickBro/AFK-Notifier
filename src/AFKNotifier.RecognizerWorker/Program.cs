using System.IO.Compression;
using System.Text.Json;
using Vosk;

namespace AFKNotifier.RecognizerWorker;

internal static class Program
{
    private const int SampleRate = 16000;
    private const string ModelName = "vosk-model-small-en-us-0.15";
    private const string ModelDownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";

    private static readonly string ModelsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AFK Notifier",
        "Models");

    private static readonly string ModelPath = Path.Combine(ModelsRoot, ModelName);

    private static async Task<int> Main()
    {
        try
        {
            global::Vosk.Vosk.SetLogLevel(-1);
            var modelPath = await EnsureModelAsync();

            using var model = new Model(modelPath);
            using var recognizer = new VoskRecognizer(model, SampleRate);
            recognizer.SetMaxAlternatives(0);
            recognizer.SetWords(true);

            WriteMessage(new { type = "ready" });

            await using var input = Console.OpenStandardInput();
            var buffer = new byte[3200];

            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer);
                if (bytesRead <= 0)
                {
                    break;
                }

                if (recognizer.AcceptWaveform(buffer, bytesRead))
                {
                    var result = ParseResult(recognizer.Result(), true);
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        WriteMessage(new
                        {
                            type = "final",
                            text = result.Text,
                            confidence = result.Confidence
                        });
                    }

                    continue;
                }

                var partial = ParseResult(recognizer.PartialResult(), false);
                if (string.IsNullOrWhiteSpace(partial.Text))
                {
                    continue;
                }

                WriteMessage(new
                {
                    type = "partial",
                    text = partial.Text,
                    confidence = 0f
                });
            }

            var finalResult = ParseResult(recognizer.FinalResult(), true);
            if (!string.IsNullOrWhiteSpace(finalResult.Text))
            {
                WriteMessage(new
                {
                    type = "final",
                    text = finalResult.Text,
                    confidence = finalResult.Confidence
                });
            }

            return 0;
        }
        catch (Exception exception)
        {
            WriteMessage(new
            {
                type = "error",
                message = exception.Message
            });
            return 1;
        }
    }

    private static RecognitionResult ParseResult(string json, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RecognitionResult(string.Empty, 0);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var propertyName = isFinal ? "text" : "partial";

        if (!root.TryGetProperty(propertyName, out var textElement))
        {
            return new RecognitionResult(string.Empty, 0);
        }

        var text = textElement.GetString()?.Trim() ?? string.Empty;
        if (!isFinal || !root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
        {
            return new RecognitionResult(text, 0);
        }

        var confidenceValues = new List<float>();
        foreach (var word in resultElement.EnumerateArray())
        {
            if (word.TryGetProperty("conf", out var confidenceElement) && confidenceElement.TryGetSingle(out var confidence))
            {
                confidenceValues.Add(confidence);
            }
        }

        return new RecognitionResult(
            text,
            confidenceValues.Count == 0 ? 0 : confidenceValues.Average());
    }

    private static async Task<string> EnsureModelAsync()
    {
        if (IsModelValid(ModelPath))
        {
            return ModelPath;
        }

        Directory.CreateDirectory(ModelsRoot);

        var archivePath = Path.Combine(ModelsRoot, ModelName + ".zip.download");
        var extractRoot = Path.Combine(ModelsRoot, ModelName + ".extract");

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, true);
        }

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(15)
            };
            using var response = await httpClient.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = File.Create(archivePath))
            {
                await source.CopyToAsync(destination);
            }

            if (new FileInfo(archivePath).Length < 20_000_000)
            {
                throw new InvalidDataException("The downloaded Vosk model archive is incomplete.");
            }

            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(archivePath, extractRoot, true);

            var extractedModel = Path.Combine(extractRoot, ModelName);
            if (!IsModelValid(extractedModel))
            {
                throw new InvalidDataException("The extracted Vosk model is incomplete.");
            }

            if (Directory.Exists(ModelPath))
            {
                Directory.Delete(ModelPath, true);
            }

            Directory.Move(extractedModel, ModelPath);
            return ModelPath;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(extractRoot))
            {
                Directory.Delete(extractRoot, true);
            }
        }
    }

    private static bool IsModelValid(string modelPath)
    {
        return File.Exists(Path.Combine(modelPath, "am", "final.mdl")) &&
            File.Exists(Path.Combine(modelPath, "conf", "model.conf"));
    }

    private static void WriteMessage(object message)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(message));
        Console.Out.Flush();
    }

    private sealed record RecognitionResult(string Text, float Confidence);
}
