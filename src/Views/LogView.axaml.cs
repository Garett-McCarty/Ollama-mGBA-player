using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace OllamaNetGB.Views;

public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();

    private async void CopySelected(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || vm.SelectedEntry is null)
            {
                vm.CopyStatus = "Select an entry first. You can also select and copy text in the details box.";
                return;
            }
            await clipboard.SetTextAsync(vm.SelectedText);
            vm.CopyStatus = "Copied selected entry.";
        }
        catch (Exception ex) { vm.CopyStatus = $"Could not copy: {ex.Message}"; }
    }
}
