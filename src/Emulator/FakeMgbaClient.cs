using System;
using System.Threading;
using System.Threading.Tasks;
using OllamaNetGB.Models;

namespace OllamaNetGB.Emulator;

public sealed class FakeMgbaClient: IMgbaClient
{
    private static readonly byte[] GrayPixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]?> GetScreenshotAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(GrayPixelPng);

    public Task TapAsync(GbaButton button, int holdMs, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[FakeMgba] Tap {button} for {holdMs}ms");
        return Task.CompletedTask;
    }

    public Task<bool> IsAliveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

}
