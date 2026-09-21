
using System.Threading.Tasks;

namespace OllamaNetGB.Config;

public interface ISettingsService
{
    AppSettings Current { get; }
    void Load();
    Task SaveAsync();
    string ConfigFilePath { get; }
}
