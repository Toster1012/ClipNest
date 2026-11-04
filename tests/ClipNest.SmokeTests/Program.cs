using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Tests;

public static class Program
{
    private static int _exitCode;

    [STAThread]
    public static int Main(string[] arguments)
    {
        if (arguments.Contains("--drop-target"))
        {
            Application dropApplication = new Application();
            System.Windows.Controls.TextBlock label = new System.Windows.Controls.TextBlock { Text = "DROP IMAGE BATCH HERE", Foreground = Brushes.White, FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Window target = new Window { Title = "ClipNest Drop Test", Width = 850, Height = 680, Left = 300, Top = 120, Background = new SolidColorBrush(Color.FromRgb(35, 60, 65)), Content = label, AllowDrop = true };
            target.DragOver += (sender, eventArgs) =>
            {
                eventArgs.Effects = eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                eventArgs.Handled = true;
            };
            target.Drop += (sender, eventArgs) =>
            {
                string[] paths = (string[])eventArgs.Data.GetData(DataFormats.FileDrop);
                string receivedDirectory = Path.Combine(Environment.CurrentDirectory, "tests", "Received");
                Directory.CreateDirectory(receivedDirectory);

                foreach (string path in paths)
                {
                    File.Copy(path, Path.Combine(receivedDirectory, Path.GetFileName(path)), true);
                }

                File.WriteAllText(Path.Combine(receivedDirectory, "receipt.txt"), string.Join(Environment.NewLine, paths));
                label.Text = $"RECEIVED {paths.Length} FILES";
                eventArgs.Effects = DragDropEffects.Copy;
                eventArgs.Handled = true;
            };
            dropApplication.Run(target);
            return 0;
        }

        if (arguments.Contains("--seed"))
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "tests", "Fixtures");
            Directory.CreateDirectory(directory);
            System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
            files.Add(CreateFixture(directory, "Lavender.png", Colors.MediumPurple));
            files.Add(CreateFixture(directory, "Ocean.png", Colors.CornflowerBlue));
            files.Add(CreateFixture(directory, "Sunset.png", Colors.Coral));
            Clipboard.SetFileDropList(files);
            Console.WriteLine("Seeded Windows clipboard with three image files.");
            return 0;
        }

        Application application = new Application();
        application.Startup += OnStartup;
        application.Run();
        return _exitCode;
    }

    private static async void OnStartup(object sender, StartupEventArgs eventArgs)
    {
        try
        {
            await RunAsync();
            Console.WriteLine("PASS: import, file identity, frozen thumbnails, deduplication, FIFO limit, corrupt input, retained transfers, cleanup.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            _exitCode = 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static async Task RunAsync()
    {
        string fixtureDirectory = Path.Combine(Path.GetTempPath(), "ClipNestTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        string retainedPath = string.Empty;
        string sessionDirectory = string.Empty;

        try
        {
            string firstPath = CreateFixture(fixtureDirectory, "first.png", Colors.MediumPurple);
            string secondPath = CreateFixture(fixtureDirectory, "second.png", Colors.CornflowerBlue);
            string thirdPath = CreateFixture(fixtureDirectory, "third.png", Colors.Coral);
            using ClipboardHistory history = new ClipboardHistory(Path.Combine(fixtureDirectory, "Sessions")) { Capacity = 2 };
            await history.ImportAsync(new[] { firstPath, secondPath }, null, CancellationToken.None);
            Require(history.Entries.Count == 2, "Batch import failed.");
            ClipboardEntry first = history.Entries.Single(entry => entry.Title == "first.png");
            sessionDirectory = Path.GetDirectoryName(first.FilePath)!;
            Require(File.ReadAllBytes(firstPath).SequenceEqual(File.ReadAllBytes(first.FilePath)), "Original file changed.");
            Require(first.Thumbnail?.IsFrozen == true, "Thumbnail is not frozen.");
            await history.ImportAsync(new[] { firstPath }, null, CancellationToken.None);
            Require(history.Entries.Count == 2 && history.Entries[0].Title == "first.png", "Duplicate handling failed.");
            await history.ImportAsync(new[] { thirdPath }, null, CancellationToken.None);
            Require(history.Entries.Count == 2 && history.Entries.All(entry => entry.Title != "second.png"), "Capacity eviction failed.");
            string invalidPath = Path.Combine(fixtureDirectory, "invalid.png");
            File.WriteAllText(invalidPath, "This is not an image.");
            await history.ImportAsync(new[] { invalidPath }, null, CancellationToken.None);
            Require(history.Entries.Count == 2 && history.Entries[0].Kind == ClipboardEntryKind.File, "Unsupported image was not retained as a file.");
            ClipboardEntry retained = history.Entries[0];
            retainedPath = retained.FilePath;
            history.RetainForTransfer(new[] { retained });
            history.Remove(new[] { retained });
            Require(File.Exists(retainedPath), "Transfer file deleted too early.");
            ClipboardEntry removed = history.Entries[0];
            history.Remove(new[] { removed });
            Require(File.Exists(removed.FilePath) == false, "Unshared file not deleted.");
            BitmapImage clipboardImage = new BitmapImage();
            clipboardImage.BeginInit();
            clipboardImage.CacheOption = BitmapCacheOption.OnLoad;
            clipboardImage.UriSource = new Uri(firstPath);
            clipboardImage.EndInit();
            clipboardImage.Freeze();
            await history.ImportAsync(Array.Empty<string>(), clipboardImage, CancellationToken.None);
            Require(history.Entries.Count == 1, "Bitmap import failed.");
            history.Dispose();
            Require(Directory.Exists(sessionDirectory) == false, "Session cache not removed.");
        }
        finally
        {
            Directory.Delete(fixtureDirectory, true);
        }
    }

    private static string CreateFixture(string directory, string fileName, Color color)
    {
        const int Width = 640;
        const int Height = 360;
        const int Dpi = 96;
        DrawingVisual visual = new DrawingVisual();

        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, Width, Height));
        }

        RenderTargetBitmap bitmap = new RenderTargetBitmap(Width, Height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, fileName);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (condition == false)
        {
            throw new InvalidOperationException(message);
        }
    }
}
