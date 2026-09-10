using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProductivityMcp.App.ViewModels;

namespace ProductivityMcp.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void AllowClose() => _allowClose = true;

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private async void ChooseCredentials_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Google OAuth desktop credentials",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON files")
                {
                    Patterns = ["*.json"],
                    MimeTypes = ["application/json"],
                },
            ],
        });

        if (files.Count == 1 && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ImportCredentialsAsync(files[0].Path.LocalPath);
        }
    }

    private void DisconnectAccount_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string accountKey } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RequestDisconnect(accountKey);
        }
    }
}
