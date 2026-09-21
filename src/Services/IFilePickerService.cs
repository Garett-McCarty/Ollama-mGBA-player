
using System.Threading.Tasks;
using Avalonia.Controls;

namespace OllamaNetGB.Services;

public interface IFilePickerService
{
    void Attach(TopLevel topLevel);

    Task<string?> PickFileAsync(string title, params string[] extensions);

    Task<string?> PickFolderAsync(string title);
}
