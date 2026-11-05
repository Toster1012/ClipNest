using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using ClipNest.Services;

namespace ClipNest;

public partial class UpdateWindow : Window
{
    private readonly ReleaseUpdate _release;
    private readonly CancellationTokenSource _download = new CancellationTokenSource();
    private bool _isInstalling;
    private bool _isDownloading;

    public UpdateWindow(ReleaseUpdate release)
    {
        InitializeComponent();
        _release = release;
        Heading.Text = $"Доступна версия {release.Version.ToString(3)}";
        Status.Text = $"Установлена {ReleaseUpdate.CurrentVersionText} · {release.Size / 1024d / 1024d:0.#} МиБ";
    }

    public void CancelForShutdown() => _download.Cancel();

    private void OnLater(object sender, RoutedEventArgs eventArgs) => Close();

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_isInstalling)
        {
            eventArgs.Cancel = true;
            return;
        }

        _download.Cancel();
    }

    private async void OnUpdate(object sender, RoutedEventArgs eventArgs)
    {
        if (_isDownloading)
        {
            return;
        }

        _isDownloading = true;
        UpdateButton.IsEnabled = false;
        LaterButton.Content = "Отмена";
        DownloadProgress.Visibility = Visibility.Visible;

        try
        {
            Progress<double> progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value * 100;
                Status.Text = $"Скачивание… {value:P0}";
            });
            string payload = await _release.DownloadAsync(progress, _download.Token);
            _download.Token.ThrowIfCancellationRequested();
            _isInstalling = true;
            LaterButton.IsEnabled = false;
            Status.Text = "Установка и перезапуск…";
            await _release.StartInstallerAsync(payload, _download.Token);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            if (IsVisible)
            {
                Status.Text = "Скачивание отменено или превышено время ожидания.";
            }
        }
        catch (Exception exception)
        {
            Status.Text = $"Не удалось обновить: {exception.Message}";
        }
        finally
        {
            _isInstalling = false;
            _isDownloading = false;
            UpdateButton.IsEnabled = _download.IsCancellationRequested == false;
            LaterButton.IsEnabled = true;
            LaterButton.Content = "Позже";
        }
    }
}
