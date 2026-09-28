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
    private long _lastHypothesisTicks;
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
            Text = "> WINDOWS DETECTION",
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
            Text = "LOCAL WINDOWS SPEECH RECOGNITION",
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
        SetDetection("STARTING WINDOWS SPEECH ENGINE...", "WAITING FOR APPLICATION AUDIO");

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
            StatusTextBlock.Text = "STARTING LOCAL SPEECH RECOGNITION...";
            _speechRecognizer = new SpeechRecognizerService(triggerPhrase);
            _speechRecognizer.TriggerDetected += OnTriggerDetected;
            _speechRecognizer.SpeechObserved += OnSpeechObserved;
            _speechRecognizer.Start();

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

            SetDetection("LISTENING...", $"TRIGGER: \"{triggerPhrase.ToUpperInvariant()}\" // MINIMUM CONFIDENCE 68%");
            StatusTextBlock.Text = $"MONITORING {application.ProcessName.ToUpperInvariant()} // WAITING FOR \"{triggerPhrase.ToUpperInvariant()}\".";
        }
        catch (Exception exception)
        {
            var error = $"COULD NOT START ({exception.GetType().Name.ToUpperInvariant()}): {exception.Message}";
            SetDetection("DETECTION UNAVAILABLE", exception.Message.ToUpperInvariant());
            await StopMonitoringAsync(false, error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync(false);
    }

    private void OnAudioDataAvailable(byte[] audioData)
    {
        _speechRecognizer?.FeedAudio(audioData);
    }

    private void OnSpeechObserved(SpeechObservation observation)
    {
        if (!observation.IsFinal)
        {
            var now = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastHypothesisTicks);
            if (now - previous < TimeSpan.FromMilliseconds(120).Ticks)
            {
                return;
            }
            Interlocked.Exchange(ref _lastHypothesisTicks, now);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var text = string.IsNullOrWhiteSpace(observation.Text)
                ? "(UNRESOLVED SPEECH)"
                : observation.Text.Trim().ToUpperInvariant();

            var prefix = observation.IsRejected
                ? "REJECTED"
                : observation.IsFinal
                    ? "HEARD"
                    : "HEARING";

            var confidence = observation.Confidence > 0
                ? $"{observation.Confidence:P0}"
                : "--";

            var state = observation.IsFinal ? "FINAL" : "LIVE";
            SetDetection(
                $"{prefix}: \"{text}\"",
                $"{observation.GrammarName.ToUpperInvariant()} // CONFIDENCE {confidence} // {state}");
        }));
    }

    private void OnTriggerDetected(string recognizedText, float confidence)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            SetDetection(
                $"TRIGGER: \"{recognizedText.ToUpperInvariant()}\"",
                $"ACCEPTED // CONFIDENCE {confidence:P0}");
            StatusTextBlock.Text = $"DETECTED {recognizedText.ToUpperInvariant()} ({confidence:P0}).";
            await StopMonitoringAsync(true);
        }));
    }

    private void SetDetection(string text, string meta)
    {
        if (_detectionTextBlock is not null)
        {
            _detectionTextBlock.Text = text;
        }

        if (_detectionMetaTextBlock is not null)
        {
            _detectionMetaTextBlock.Text = meta;
        }
    }

    private async Task StopMonitoringAsync(bool confirmed, string? finalStatus = null)
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        var outputDevice = OutputComboBox.SelectedItem as AudioDeviceOption;

        try
        {
            _alertCancellation?.Cancel();
            await _captureService.StopAsync();
            _captureService.AudioDataAvailable -= OnAudioDataAvailable;

            if (_speechRecognizer is not null)
            {
                _speechRecognizer.TriggerDetected -= OnTriggerDetected;
                _speechRecognizer.SpeechObserved -= OnSpeechObserved;
                _speechRecognizer.Dispose();
                _speechRecognizer = null;
            }

            if (_alertTask is not null)
            {
                try
                {
                    await _alertTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _alertTask = null;
            _alertCancellation?.Dispose();
            _alertCancellation = null;

            if (confirmed && outputDevice is not null)
            {
                await _notifierService.PlayConfirmationAsync(
                    outputDevice.DeviceId,
                    _settings.ConfirmationBeepPitchHz,
                    _settings.ConfirmationBeepVolumePercent / 100.0);
                StatusTextBlock.Text = "AFK CONFIRMED // ALERT STOPPED.";
            }
            else if (!string.IsNullOrWhiteSpace(finalStatus))
            {
                StatusTextBlock.Text = finalStatus;
            }
            else
            {
                StatusTextBlock.Text = "STOPPED.";
                if (_detectionMetaTextBlock is not null)
                {
                    _detectionMetaTextBlock.Text = "MONITOR STOPPED // LAST WINDOWS RESULT SHOWN";
                }
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
