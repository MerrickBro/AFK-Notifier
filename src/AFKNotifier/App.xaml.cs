using System.Threading;
using System.Windows;

namespace AFKNotifier;

public partial class App : Application
{
    private const string MutexName = @"Local\AFKNotifier.SingleInstance";
    private const string ActivationEventName = @"Local\AFKNotifier.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _instanceMutex = new Mutex(true, MutexName, out var createdNew);

        if (!createdNew)
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
        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        _activationCancellation?.Dispose();
        _activationEvent?.Dispose();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
