using System.Windows;
using System.Windows.Threading;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.App;

public partial class App : Application
{
    public static ProcessHardeningReport HardeningReport { get; private set; } =
        new(false, false, false, false);

    protected override void OnStartup(StartupEventArgs e)
    {
        HardeningReport = ProcessHardening.Initialize();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string safe = LogRedactor.Redact(e.Exception.Message);
        MessageBox.Show(
            $"Непредвиденная ошибка (детали очищены):\n{safe}",
            "Aegis Security Guard",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _ = LogRedactor.Redact(ex.Message);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }
}
