using System.Threading;

namespace CyberSnap.UI;

internal sealed class OcrResultWindowLifecycle
{
    private int _closeRequested;
    private bool _isPinned;

    public bool IsCloseRequested => Volatile.Read(ref _closeRequested) == 1;

    public bool TryBeginClose() => Interlocked.Exchange(ref _closeRequested, 1) == 0;

    public void SetPinned(bool pinned) => _isPinned = pinned;

    public bool ShouldCloseOnDeactivate(bool isLoaded, bool isMinimized) =>
        isLoaded && !isMinimized && !IsCloseRequested && !_isPinned;
}
