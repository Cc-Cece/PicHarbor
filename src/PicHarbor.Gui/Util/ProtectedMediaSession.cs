namespace PicHarbor.Gui.Util;

internal static class ProtectedMediaSession
{
    internal static bool IsLocked { get; private set; }
    internal static event Action? Changed;
    internal static void SetLocked(bool locked)
    {
        if (IsLocked == locked) return;
        IsLocked = locked;
        Changed?.Invoke();
    }
}
