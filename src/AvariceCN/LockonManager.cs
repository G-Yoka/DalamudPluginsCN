using ECommons.GameHelpers;
using ECommons.Reflection;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Avarice;

internal static unsafe class LockonManager
{
    private const uint SuccessVfx = 136;
    private const uint FailureVfx = 137;

    internal static void DisplayIcon(bool success)
    {
        if(!Player.Available) return;
        try
        {
            P.memory.ShowLockonIcon(&Player.BattleChara->Vfx, success ? SuccessVfx : FailureVfx, Player.Object.GameObjectId);
        }
        catch (Exception e)
        {
            PluginLog.Error($"{nameof(DisplayIcon)} error: {e.ToStringFull()}");
        }
    }
}
