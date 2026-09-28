using System.Windows;
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
    private bool _isStopping;

    public MainWindow()
    {
        InitializeComponent();
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
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            VersionTextBlock.Text = $"V{update.Version} // DOWNLOADING";
            await _updateService.StageUpdateAsync(update);
            VersionTextBlock.Text = $"V{update.Version} // READY";
            VersionTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xFF, 0xE0));
        }
        catch
        {
            VersionTextBlock.Text = $"V{_updateService.CurrentVersionDisplay}";
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

            StatusTextBlock.Text = $"MONITORING {application.ProcessName.ToUpperInvariant()} // WAITING FOR \"{triggerPhrase.ToUpperInvariant()}\".";
        }
        catch (Exception exception)
        {
            var error = $"COULD NOT START ({exception.GetType().Name.ToUpperInvariant()}): {exception.Message}";
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

    private void OnTriggerDetected(string recognizedText, float confidence)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            StatusTextBlock.Text = $"DETECTED {recognizedText.ToUpperInvariant()} ({confidence:P0}).";
            await StopMonitoringAsync(true);
        }));
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
