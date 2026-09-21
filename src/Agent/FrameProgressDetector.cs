using System.Security.Cryptography;
using OllamaNetGB.Models;

namespace OllamaNetGB.Agent;

/// <summary>
/// Conservatively detects exact repeated frames and repeated controls. The
/// cognitive evaluator supplies the higher-level semantic progress signal.
/// </summary>
public sealed class FrameProgressDetector
{
    private string? _lastFrameHash;
    private GbaButton? _lastButton;
    private int _unchangedFrames;
    private int _repeatedButtons;

    public FrameProgressState Observe(byte[] png)
    {
        var hash = Convert.ToHexString(SHA256.HashData(png));
        var changed = !string.Equals(hash, _lastFrameHash, StringComparison.Ordinal);
        _unchangedFrames = changed ? 0 : _unchangedFrames + 1;
        _lastFrameHash = hash;

        return new FrameProgressState(
            changed,
            Math.Max(_unchangedFrames, Math.Max(0, _repeatedButtons - 3)),
            hash);
    }

    public void RecordAction(GbaButton button)
    {
        _repeatedButtons = _lastButton == button ? _repeatedButtons + 1 : 0;
        _lastButton = button;
    }

    public void MarkProgress()
    {
        _unchangedFrames = 0;
        _repeatedButtons = 0;
    }
}

public sealed record FrameProgressState(bool Changed, int StuckCount, string FrameHash);
