using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AFKNotifier.Models;
using AFKNotifier.Services;

namespace AFKNotifier;

public partial class MainWindow : Window
{
    private readonly ProcessService _processService = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly ProcessAudioCaptureService _captureService = new();
    private readonly NotifierService _notifierService = new();
    private readonly SettingsService _settingsService = new();
    private readonly UpdateService _updateService = UpdateService.Instance;

    private SpeechRecognizerService? _speechRecognizer;
    private CancellationTokenSource? _alertCancellation;
    private Task? _alertTask;
    private AppSettings _settings = new();
    private TextBlock? _detectionTextBlock;
    private TextBlock? _detectionMetaTextBlock;
    private string _detectionDetail = "LOCAL STREAMING TRANSCRIPTION";
    private double _latestInputDbFs = -120;
    private long _lastInputUiTicks;
    private bool _isStopping;

    public MainWindow()
    {
        InitializeComponent();
        InstallDetectionPanel();
        VersionTextBlock.Cursor = Cursors.Hand;
        VersionTextBlock.ToolTip = "Click to check for updates";
        VersionTextBlock.MouseLeftButtonUp += VersionTextBlock_MouseLeftButtonUp;
    }

    private void InstallDetectionPanel()
    {
        if (StartButton.Parent is not StackPanel buttonRow || buttonRow.Parent is not Grid contentGrid)
        {
            return;
        }

        var accentBrush = (Brush)FindResource("AccentBrush");

        var header = new TextBlock
        {
            Text = "> LOCAL TRANSCRIPTION",
            Foreground = accentBrush,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 3)
        };

        _detectionTextBlock = new TextBlock
        {
            Text = "WAITING FOR MONITOR...",
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        _detectionMetaTextBlock = new TextBlock
        {
            Text = "LOCAL STREAMING TRANSCRIPTION",
            FontSize = 11,
            Opacity = 0.62,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_detectionTextBlock);
        stack.Children.Add(_detectionMetaTextBlock);

        var panel = new Border
        {
            Style = (Style)FindResource("SectionBorderStyle"),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 0, 9),
            Child = stack
        };

        Grid.SetRow(panel, 8);
        contentGrid.Children.Add(panel);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        TriggerTextBox.Text = _settings.TriggerPhrase;
        IntervalTextBox.Text = _settings.BeepIntervalSeconds.ToString("0.###");
        IdleVolumeTextBox.Text = _settings.IdleBeepVolumePercent.ToString("0.##");
        IdlePitchTextBox.Text = _settings.IdleBeepPitchHz.ToString("0.##");
        ConfirmationVolumeTextBox.Text = _settings.ConfirmationBeepVolumePercent.ToString("0.##");
        ConfirmationPitchTextBox.Text = _settings.ConfirmationBeepPitchHz.ToString("0.##");
        VersionTextBlock.Text = $"V{_updateService.CurrentVersionDisplay}";
        RefreshSelections();
        await CheckForUpdatesAsync(false);
    }

    private async void VersionTextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await CheckForUpdatesAsync(true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateService.HasStagedUpdate)
        {
            if (userInitiated)
            {
                StatusTextBlock.Text = "UPDATE ALREADY DOWNLOADED // CLOSE AFK NOTIFIER TO INSTALL.";
            }
            return;
        }

        var accent = new SolidColorBrush(Color.FromRgb(0xB0, 0xFF, 0xE0));
        VersionTextBlock.Foreground = accent;

        if (userInitiated)
        {
            StatusTextBlock.Text = "CHECKING GITHUB FOR UPDATES...";
        }

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                VersionTextBlock.Text = $"V{_updateService.CurrentVersionDisplay}";
                if (userInitiated)
                {
                    StatusTextBlock.Text = $"UP TO DATE // V{_updateService.CurrentVersionDisplay}.";
                }
                return;
            }

            VersionTextBlock.Text = $"V{update.Version} // DOWNLOADING";
            StatusTextBlock.Text = $"UPDATE {update.TagName.ToUpperInvariant()} FOUND // DOWNLOADING {update.AssetName.ToUpperInvariant()}...";

            await _updateService.StageUpdateAsync(update);

            VersionTextBlock.Text = $"V{update.Version} // READY";
            StatusTextBlock.Text = $"UPDATE {update.TagName.ToUpperInvariant()} READY // CLOSE AFK NOTIFIER TO INSTALL AND RESTART.";
        }
        catch (Exception exception)
        {
            VersionTextBlock.Text = $"V{_updateService.CurrentVersionDisplay} // UPDATE ERROR";
            if (userInitiated)
            {
                StatusTextBlock.Text = $"UPDATE CHECK FAILED ({exception.GetType().Name.ToUpperInvariant()}): {exception.Message}";
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSelections();
    }

    private void RefreshSelections()
    {
        var selectedProcessName = (ApplicationComboBox.SelectedItem as AudioProcessOption)?.ProcessName
            ?? _settings.ProcessName;
        var selectedDeviceId = (OutputComboBox.SelectedItem as AudioDeviceOption)?.DeviceId
            ?? _settings.OutputDeviceId;

        var applications = _processService.GetApplications();
        var outputDevices = _audioDeviceService.GetOutputDevices();

        ApplicationComboBox.ItemsSource = applications;
        OutputComboBox.ItemsSource = outputDevices;

        ApplicationComboBox.SelectedItem = applications.FirstOrDefault(application =>
            application.ProcessName.Equals(selectedProcessName, StringComparison.OrdinalIgnoreCase));
        OutputComboBox.SelectedItem = outputDevices.FirstOrDefault(device => device.DeviceId == selectedDeviceId);

        ApplicationComboBox.SelectedItem ??= applications.FirstOrDefault();
        OutputComboBox.SelectedItem ??= outputDevices.FirstOrDefault();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationComboBox.SelectedItem is not AudioProcessOption application)
        {
            StatusTextBlock.Text = "SELECT AN APPLICATION TO MONITOR.";
            return;
        }

        if (OutputComboBox.SelectedItem is not AudioDeviceOption outputDevice)
        {
            StatusTextBlock.Text = "SELECT AN ALERT OUTPUT DEVICE.";
            return;
        }

        var triggerPhrase = TriggerTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(triggerPhrase))
        {
            StatusTextBlock.Text = "ENTER A TRIGGER PHRASE.";
            return;
        }

        if (!TryParseRange(IntervalTextBox.Text, 0.1, 60, out var intervalSeconds))
        {
            StatusTextBlock.Text = "BEEP INTERVAL MUST BE BETWEEN 0.1 AND 60 SECONDS.";
            return;
        }

        if (!TryParseRange(IdleVolumeTextBox.Text, 0, 100, out var idleVolumePercent))
        {
            StatusTextBlock.Text = "IDLE BEEP VOLUME MUST BE BETWEEN 0 AND 100%.";
            return;
        }

        if (!TryParseRange(ConfirmationVolumeTextBox.Text, 0, 100, out var confirmationVolumePercent))
        {
            StatusTextBlock.Text = "CONFIRMATION VOLUME MUST BE BETWEEN 0 AND 100%.";
            return;
        }

        if (!TryParseRange(IdlePitchTextBox.Text, 50, 5000, out var idlePitchHz))
        {
            StatusTextBlock.Text = "IDLE BEEP PITCH MUST BE BETWEEN 50 AND 5000 HZ.";
            return;
        }

        if (!TryParseRange(ConfirmationPitchTextBox.Text, 50, 5000, out var confirmationPitchHz))
        {
            StatusTextBlock.Text = "CONFIRMATION PITCH MUST BE BETWEEN 50 AND 5000 HZ.";
            return;
        }

        SetMonitoringState(true);
        StatusTextBlock.Text = $"STARTING MONITOR FOR {application.ProcessName.ToUpperInvariant()}...";
        SetDetection("STARTING STREAMING RECOGNITION...", "VOSK SMALL EN-US // WAITING FOR MODEL");

        _settings = new AppSettings
        {
            ProcessName = application.ProcessName,
            OutputDeviceId = outputDevice.DeviceId,
            TriggerPhrase = triggerPhrase,
            BeepIntervalSeconds = intervalSeconds,
            IdleBeepVolumePercent = idleVolumePercent,
            IdleBeepPitchHz = idlePitchHz,
            ConfirmationBeepVolumePercent = confirmationVolumePercent,
            ConfirmationBeepPitchHz = confirmationPitchHz
        };
        _settingsService.Save(_settings);

        try
        {
            _speechRecognizer = new SpeechRecognizerService(triggerPhrase);
            _speechRecognizer.TriggerDetected += OnTriggerDetected;
            _speechRecognizer.SpeechObserved += OnSpeechObserved;
            _speechRecognizer.InputLevelUpdated += OnInputLevelUpdated;
            _speechRecognizer.RecognitionFaulted += OnRecognitionFaulted;

            if (_speechRecognizer.RequiresModelDownload)
            {
                StatusTextBlock.Text = "DOWNLOADING LOCAL VOSK MODEL // FIRST RUN ONLY...";
                SetDetection("DOWNLOADING SPEECH MODEL...", "VOSK SMALL EN-US // STORED LOCALLY AFTER DOWNLOAD");
            }
            else
            {
                StatusTextBlock.Text = "STARTING LOCAL STREAMING RECOGNITION...";
            }

            await _speechRecognizer.StartAsync();

            StatusTextBlock.Text = $"CONNECTING TO {application.ProcessName.ToUpperInvariant()} AUDIO...";
            _captureService.AudioDataAvailable += OnAudioDataAvailable;
            await _captureService.StartAsync(application.ProcessId);

            _alertCancellation = new CancellationTokenSource();
            _alertTask = _notifierService.RunAlertAsync(
                outputDevice.DeviceId,
                TimeSpan.FromSeconds(intervalSeconds),
                idlePitchHz,
                idleVolumePercent / 100.0,
                _alertCancellation.Token);

            SetDetection(
                "LISTENING...",
                $"VOSK STREAMING // TRIGGER \"{triggerPhrase.ToUpperInvariant()}\" // LIVE PARTIAL RESULTS");
            StatusTextBlock.Text = $"MONITORING {application.ProcessName.ToUpperInvariant()} // WAITING FOR \"{triggerPhrase.ToUpperInvariant()}\".";
        }
        catch (OperationCanceledException) when (_isStopping || _speechRecognizer is null)
        {
        }
        catch (Exception exception)
        {
            var error = $"COULD NOT START ({exception.GetType().Name.ToUpperInvariant()}): {exception.Message}";
            SetDetection("TRANSCRIPTION UNAVAILABLE", exception.Message.ToUpperInvariant());
            await StopMonitoringAsync(false, error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopMonitoringAsync(false);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"STOPPED // CLEANUP WARNING: {exception.Message}";
            SetMonitoringState(false);
            _isStopping = false;
        }
    }

    private void OnAudioDataAvailable(byte[] audioData)
    {
        _speechRecognizer?.FeedAudio(audioData);
    }

    private void OnInputLevelUpdated(double dbFs)
    {
        _latestInputDbFs = dbFs;

        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastInputUiTicks);
        if (now - previous < TimeSpan.FromMilliseconds(150).Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastInputUiTicks, now);
        Dispatcher.BeginInvoke(new Action(UpdateDetectionMeta));
    }

    private void OnSpeechObserved(SpeechObservation observation)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!observation.IsSpeech || string.IsNullOrWhiteSpace(observation.Text))
            {
                return;
            }

            var text = observation.Text.Trim().ToUpperInvariant();

            if (observation.MatchesTrigger)
            {
                SetDetection(
                    $"TRIGGER HEARD: \"{text}\"",
                    observation.TriggerVerified
                        ? "VOSK STREAMING // VERIFIED"
                        : "VOSK STREAMING // VERIFYING LIVE PARTIAL RESULT");
                return;
            }

            var detail = observation.IsFinal
                ? observation.Confidence > 0
                    ? $"VOSK STREAMING // FINAL // CONFIDENCE {observation.Confidence:P0}"
                    : "VOSK STREAMING // FINAL"
                : "VOSK STREAMING // LIVE PARTIAL";

            SetDetection($"HEARD: \"{text}\"", detail);
        }));
    }

    private void OnRecognitionFaulted(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetDetection("RECOGNIZER WORKER STOPPED", message.ToUpperInvariant());
            StatusTextBlock.Text = "SPEECH RECOGNIZER FAILED // AFK NOTIFIER REMAINS RUNNING // STOP AND START TO RETRY.";
        }));
    }

    private void OnTriggerDetected(string recognizedText, float confidence)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            SetDetection(
                $"TRIGGER CONFIRMED: \"{recognizedText.ToUpperInvariant()}\"",
                confidence > 0
                    ? $"VOSK STREAMING // VERIFIED TRIGGER // CONFIDENCE {confidence:P0}"
                    : "VOSK STREAMING // VERIFIED LIVE TRIGGER");
            StatusTextBlock.Text = $"DETECTED {recognizedText.ToUpperInvariant()}.";
            await StopMonitoringAsync(true);
        }));
    }

    private void SetDetection(string text, string detail)
    {
        _detectionDetail = detail;

        if (_detectionTextBlock is not null)
        {
            _detectionTextBlock.Text = text;
        }

        UpdateDetectionMeta();
    }

    private void UpdateDetectionMeta()
    {
        if (_detectionMetaTextBlock is null)
        {
            return;
        }

        var levelState = _latestInputDbFs switch
        {
            > -3 => "HOT",
            < -48 => "LOW",
            _ => "OK"
        };

        _detectionMetaTextBlock.Text = $"{_detectionDetail} // INPUT {_latestInputDbFs:0} DBFS {levelState}";
    }

    private async Task StopMonitoringAsync(bool confirmed, string? finalStatus = null)
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        StopButton.IsEnabled = false;
        StatusTextBlock.Text = confirmed ? "TRIGGER VERIFIED // STOPPING..." : "STOPPING...";

        var outputDevice = OutputComboBox.SelectedItem as AudioDeviceOption;
        var cleanupWarnings = new List<string>();
        var alertCancellation = _alertCancellation;
        var alertTask = _alertTask;
        var speechRecognizer = _speechRecognizer;

        _alertCancellation = null;
        _alertTask = null;
        _speechRecognizer = null;

        _captureService.AudioDataAvailable -= OnAudioDataAvailable;

        if (speechRecognizer is not null)
        {
            speechRecognizer.TriggerDetected -= OnTriggerDetected;
            speechRecognizer.SpeechObserved -= OnSpeechObserved;
            speechRecognizer.InputLevelUpdated -= OnInputLevelUpdated;
            speechRecognizer.RecognitionFaulted -= OnRecognitionFaulted;
        }

        try
        {
            try
            {
                alertCancellation?.Cancel();
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add($"ALERT CANCEL: {exception.Message}");
            }

            try
            {
                await _captureService.StopAsync();
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add($"AUDIO CAPTURE: {exception.Message}");
            }

            if (speechRecognizer is not null)
            {
                try
                {
                    await speechRecognizer.StopAsync();
                }
                catch (Exception exception)
                {
                    cleanupWarnings.Add($"TRANSCRIPTION: {exception.Message}");
                }

                try
                {
                    speechRecognizer.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupWarnings.Add($"TRANSCRIPTION DISPOSE: {exception.Message}");
                }
            }

            if (alertTask is not null)
            {
                try
                {
                    await alertTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    cleanupWarnings.Add($"ALERT AUDIO: {exception.Message}");
                }
            }

            try
            {
                alertCancellation?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add($"ALERT DISPOSE: {exception.Message}");
            }

            if (confirmed && outputDevice is not null)
            {
                try
                {
                    await _notifierService.PlayConfirmationAsync(
                        outputDevice.DeviceId,
                        _settings.ConfirmationBeepPitchHz,
                        _settings.ConfirmationBeepVolumePercent / 100.0);
                    StatusTextBlock.Text = "AFK CONFIRMED // ALERT STOPPED.";
                }
                catch (Exception exception)
                {
                    cleanupWarnings.Add($"CONFIRMATION AUDIO: {exception.Message}");
                    StatusTextBlock.Text = "AFK CONFIRMED // ALERT STOPPED // CONFIRMATION AUDIO FAILED.";
                }
            }
            else if (!string.IsNullOrWhiteSpace(finalStatus))
            {
                StatusTextBlock.Text = finalStatus;
            }
            else
            {
                StatusTextBlock.Text = "STOPPED.";
                _detectionDetail = "MONITOR STOPPED // LAST STREAMING TRANSCRIPT SHOWN";
                UpdateDetectionMeta();
            }

            if (cleanupWarnings.Count > 0 && !confirmed && string.IsNullOrWhiteSpace(finalStatus))
            {
                StatusTextBlock.Text = $"STOPPED // CLEANUP WARNING: {cleanupWarnings[0]}";
            }
        }
        finally
        {
            SetMonitoringState(false);
            _isStopping = false;
        }
    }

    private void SetMonitoringState(bool monitoring)
    {
        StartButton.IsEnabled = !monitoring;
        StopButton.IsEnabled = monitoring;
        RefreshButton.IsEnabled = !monitoring;
        ApplicationComboBox.IsEnabled = !monitoring;
        OutputComboBox.IsEnabled = !monitoring;
        TriggerTextBox.IsEnabled = !monitoring;
        IntervalTextBox.IsEnabled = !monitoring;
        IdleVolumeTextBox.IsEnabled = !monitoring;
        IdlePitchTextBox.IsEnabled = !monitoring;
        ConfirmationVolumeTextBox.IsEnabled = !monitoring;
        ConfirmationPitchTextBox.IsEnabled = !monitoring;
    }

    private static bool TryParseRange(string text, double minimum, double maximum, out double value)
    {
        return double.TryParse(text, out value) && value >= minimum && value <= maximum;
    }
}
