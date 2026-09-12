using Dalamud.Plugin.Services;

namespace CrescentCompass.Services;

public sealed unsafe class ZoneServerIdReader
{
    private const string ContentReplyManagerSignature =
        "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 45 33 C0 48 8D 57 ?? 41 8B CE E8 ?? ?? ?? ?? 48 8D 8F";
    private const string ZoneServerIdOffsetSignature =
        "0F 11 83 ?? ?? ?? ?? 0F 10 4F ?? 0F 11 8B ?? ?? ?? ?? 0F 10 47 ?? 0F 11 83 ?? ?? ?? ?? 0F 10 4F ?? 0F 11 8B ?? ?? ?? ?? 0F 10 47 ?? 0F 11 83 ?? ?? ?? ?? 0F 10 4F ?? 0F 11 8B ?? ?? ?? ?? 0F 10 47 ?? 0F 11 83 ?? ?? ?? ?? 0F 10 4F";

    private readonly nint contentReplyManager;
    private readonly nint zoneServerIdOffset;

    public ZoneServerIdReader(ISigScanner sigScanner, IPluginLog log)
    {
        try
        {
            contentReplyManager = sigScanner.GetStaticAddressFromSig(ContentReplyManagerSignature);
            zoneServerIdOffset = sigScanner.GetStaticAddressFromSig(ZoneServerIdOffsetSignature);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Unable to resolve the zone server ID reader.");
        }
    }

    public uint Value
    {
        get
        {
            if (contentReplyManager == nint.Zero || zoneServerIdOffset == nint.Zero) return 0;
            var address = contentReplyManager + zoneServerIdOffset;
            var high = *(ushort*)address;
            var low = *(ushort*)(address + 4);
            return (uint)(high << 16 | low);
        }
    }
}
