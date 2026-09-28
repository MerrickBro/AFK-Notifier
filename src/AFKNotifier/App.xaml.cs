using System.Threading;
using System.Windows;
using AFKNotifier.Services;

namespace AFKNotifier;

public partial class App : Application
{
    private const string MutexName = @"Local\AFKNotifier.SingleInstance";
    private const string ActivationEventName = @"Local\AFKNotifier.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeFontService.ApplyWebsiteFont();

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _instanceMutex = new Mutex(true, MutexName, out _ownsInstance);

        if (!_ownsInstance)
        {
            _activationEvent.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _activationCancellation = new CancellationTokenSource();
        _ = Task.Run(() => WaitForActivation(window, _activationCancellation.Token));
    }

    private void WaitForActivation(Window window, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _activationEvent?.WaitOne();

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Dispatcher.Invoke(() => BringToFront(window));
        }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Topmost = true;
        window.Activate();
        window.Focus();
        window.Topmost = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance)
        {
            _activationCancellation?.Cancel();
            _activationEvent?.Set();
            _activationCancellation?.Dispose();

            if (_instanceMutex is not null)
            {
                _instanceMutex.ReleaseMutex();
            }
        }

        _activationEvent?.Dispose();
        _instanceMutex?.Dispose();

        UpdateService.Instance.TryLaunchPendingUpdate();

        base.OnExit(e);
    }
}
