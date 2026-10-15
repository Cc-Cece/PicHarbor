namespace PicHarbor.Gui.Updates;

/// <summary>Blocks new archive operations only during the final installation handshake.</summary>
internal static class UpdateWorkGate
{
    private static int installing;
    public static bool IsInstalling
    {
        get => Volatile.Read(ref installing) != 0;
        set => Volatile.Write(ref installing, value ? 1 : 0);
    }
}
