using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Kea.Core;

namespace Kea.Gui;

public partial class MainWindow : Window
{
    private const string HelpUrl = "https://github.com/RustingRobot/Kea#how-to-use";

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm) vm.MessageRequested += ShowMessageAsync;
    }

    private Task ShowMessageAsync(string title, string message)
        => MessageDialog.ShowAsync(this, title, message);

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        try
        {
            await vm.StartAsync();
        }
        catch (Exception ex)
        {
            // An async void handler must not let anything escape, or it takes the process with it.
            await MessageDialog.ShowAsync(this, "Something went wrong", ex.Message);
        }
    }

    private async void OnSelectFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        try
        {
            // The original opened a file dialog with a dummy "Folder Selection" filename, because
            // WinForms had no usable folder picker. Every platform Avalonia supports has a real one.
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Select a folder to save into",
                    AllowMultiple = false,
                });

            if (folders.Count == 0) return;

            string? path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                // Sandboxed or remote locations have no local path Kea could write to.
                await MessageDialog.ShowAsync(this, "Unsupported folder",
                    "That location has no local path. Please pick a folder on this machine.");
                return;
            }

            vm.SavePath = path;
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Could not open the folder picker", ex.Message);
        }
    }

    private async void OnHelp(object? sender, RoutedEventArgs e)
    {
        if (PlatformLauncher.TryOpen(HelpUrl)) return;

        await MessageDialog.ShowAsync(this, "Could not open your browser",
            $"Please visit:{Environment.NewLine}{HelpUrl}");
    }
}
