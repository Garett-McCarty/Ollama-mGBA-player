using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace OllamaNetGB.Views;

public sealed class EmulatorViewModel : INotifyPropertyChanged
{
    private IImage? _frame;
    private string _status = "No ROM loaded";

    public IImage? Frame
    {
        get => _frame;
        set => SetField(ref _frame, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}