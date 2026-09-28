using System.Windows;
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

    private SpeechRecognizerService? _speechRecognizer;
    private CancellationTokenSource? _alertCancellation;
    private Task? _alertTask;
    private AppSettings _settings = new();
    private bool _isStopping;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        TriggerTextBox.Text = _settings.TriggerPhrase;
        IntervalTextBox.Text = _settings.BeepIntervalSeconds.ToString("0.###");
        RefreshSelections();
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
            StatusTextBlock.Text = "Select an application to monitor.";
            return;
        }

        if (OutputComboBox.SelectedItem is not AudioDeviceOption outputDevice)
        {
            StatusTextBlock.Text = "Select an alert output device.";
            return;
        }

        var triggerPhrase = TriggerTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(triggerPhrase))
        {
            StatusTextBlock.Text = "Enter a trigger phrase.";
            return;
        }

        if (!double.TryParse(IntervalTextBox.Text, out var intervalSeconds) ||
            intervalSeconds < 0.1 || intervalSeconds > 60)
        {
            StatusTextBlock.Text = "Beep interval must be between 0.1 and 60 seconds.";
            return;
        }

        SetMonitoringState(true);
        StatusTextBlock.Text = $"Starting monitor for {application.ProcessName}...";

        _settings = new AppSettings
        {
            ProcessName = application.ProcessName,
            OutputDeviceId = outputDevice.DeviceId,
            TriggerPhrase = triggerPhrase,
            BeepIntervalSeconds = intervalSeconds
        };
        _settingsService.Save(_settings);

        try
        {
            StatusTextBlock.Text = "Starting local speech recognition...";
            _speechRecognizer = new SpeechRecognizerService(triggerPhrase);
            _speechRecognizer.TriggerDetected += OnTriggerDetected;
            _speechRecognizer.Start();

            StatusTextBlock.Text = $"Connecting to {application.ProcessName} audio...";
            _captureService.AudioDataAvailable += OnAudioDataAvailable;
            await _captureService.StartAsync(application.ProcessId);

            _alertCancellation = new CancellationTokenSource();
            _alertTask = _notifierService.RunAlertAsync(
                outputDevice.DeviceId,
                TimeSpan.FromSeconds(intervalSeconds),
                _alertCancellation.Token);

            StatusTextBlock.Text = $"Monitoring {application.ProcessName}. Waiting for \"{triggerPhrase}\".";
        }
        catch (Exception exception)
        {
            var error = $"Could not start ({exception.GetType().Name}): {exception.Message}";
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
            StatusTextBlock.Text = $"Detected {recognizedText} ({confidence:P0}).";
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
                await _notifierService.PlayConfirmationAsync(outputDevice.DeviceId);
                StatusTextBlock.Text = "AFK confirmed. Alert stopped.";
            }
            else if (!string.IsNullOrWhiteSpace(finalStatus))
            {
                StatusTextBlock.Text = finalStatus;
            }
            else
            {
                StatusTextBlock.Text = "Stopped.";
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
    }
}
