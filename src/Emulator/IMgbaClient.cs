
using System.Threading;
using System.Threading.Tasks;
using OllamaNetGB.Models;

namespace OllamaNetGB.Emulator;

public interface IMgbaClient
{
    Task EnsureReadyAsync(CancellationToken cancellationToken);
    Task<byte[]?> GetScreenshotAsync(CancellationToken cancellationToken);
    Task TapAsync(GbaButton button, int holdMs, CancellationToken cancellationToken);
    Task<bool> IsAliveAsync(CancellationToken cancellationToken);
}
