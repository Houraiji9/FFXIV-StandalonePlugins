namespace BatchTrade;

/// <summary>
/// CN client 2026.10.06.0000.0000 only. Pointer comparisons, never dereferencing these unpublished members.
/// Static executable review identified InventoryManager's embedded AtkEventInterface receivers:
/// item quantity at +0x3708, final trade confirmation at +0x36E8, both event kind 0.
/// Verified executable SHA-256: 958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9.
/// Public InventoryManager layout: FFXIVClientStructs 87d51a788ee33329f20b06243be332dd4d35b13c, size 0x3730.
/// See docs/batchtrade-native-evidence-v011.md. A client update requires renewed verification, not a wildcard version.
/// </summary>
public static class GameCompatibility
{
    public const string RequiredVersion = "2026.10.06.0000.0000";
    public const int InventoryManagerSize = 0x3730;
    public const int QuantityReceiverOffset = 0x3708;
    public const int FinalReceiverOffset = 0x36E8;

    public static bool IsSupported(string? version, int layoutSize) =>
        string.Equals(version?.Trim(), RequiredVersion, StringComparison.Ordinal) && layoutSize == InventoryManagerSize;

    public static bool IsQuantityReceiver(nint manager, nint receiver, ulong eventKind) =>
        manager != 0 && receiver == manager + QuantityReceiverOffset && eventKind == 0;

    public static bool IsFinalReceiver(nint manager, nint receiver, ulong eventKind) =>
        manager != 0 && receiver == manager + FinalReceiverOffset && eventKind == 0;
}
