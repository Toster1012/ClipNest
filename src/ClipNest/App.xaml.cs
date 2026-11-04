using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace ClipNest;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        _instanceMutex = new Mutex(true, "Local\\ClipNest.Desktop", out bool isNewInstance);

        if (isNewInstance == false)
        {
            MessageBox.Show("ClipNest уже работает. Нажмите Win+V или откройте его через значок возле часов.", "ClipNest");
            Shutdown();
            return;
        }

        try
        {
            LibraryWindow window = new LibraryWindow();
            MainWindow = window;
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

            if (Array.Exists(eventArgs.Args, argument => argument == "--background") == false)
            {
                window.ShowPopup();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Не удалось запустить ClipNest");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        (MainWindow as LibraryWindow)?.ShutdownResources();
        _instanceMutex?.Dispose();
        base.OnExit(eventArgs);
    }
}
