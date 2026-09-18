using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Kea.Gui;

/// <summary>
/// A plain modal message box.
/// </summary>
/// <remarks>
/// Replaces <c>MessageBox.Show</c>, which is part of Windows Forms. Avalonia deliberately ships no
/// message box of its own, so this is the smallest thing that serves the same purpose.
/// </remarks>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public static Task ShowAsync(Window owner, string title, string message)
    {
        MessageDialog dialog = new();
        dialog.FindControl<TextBlock>("TitleText")!.Text = title;
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        return dialog.ShowDialog(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
