
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OllamaNetGB.Views;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly GameViewModel _gameViewModel;
    public LogViewModel Logs { get; } = new();

    [ObservableProperty] private ObservableObject _currentPage;

    public MainWindowViewModel(SettingsViewModel settingsViewModel, GameViewModel gameViewModel)
    {
        _settingsViewModel = settingsViewModel;
        _gameViewModel = gameViewModel;
        _currentPage = settingsViewModel;
        settingsViewModel.SaveCompleted += (_, _) => ShowGameView();
    }

    [RelayCommand] private void ShowLogs() => CurrentPage = Logs;
    [RelayCommand] private void ShowSettings() => CurrentPage = _settingsViewModel;
    [RelayCommand] private void ShowGameView() => CurrentPage = _gameViewModel;
}
