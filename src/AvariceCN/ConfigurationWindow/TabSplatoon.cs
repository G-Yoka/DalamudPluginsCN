using Dalamud.Interface.Components;

namespace Avarice.ConfigurationWindow
{
    internal static class TabSplatoon
    {
        internal static void Draw()
        {
            if(ImGui.Checkbox("进入危险区域时更改颜色", ref P.config.SplatoonUnsafePixel))
            {
                WriteRequest();
            }
            ImGuiComponents.HelpMarker("当 Splatoon 将当前位置标记为危险时，把玩家受击判定点改为警告色。需要启用玩家受击判定点，并由 Splatoon 提供危险区域数据。");
            ImGui.ColorEdit4("危险区域颜色", ref P.config.SplatoonPixelCol, ImGuiColorEditFlags.NoInputs);
            ImGuiComponents.HelpMarker("站在 Splatoon 危险区域中时使用的颜色。");
            ImGuiEx.TextWrapped("仅适用于将自身标记为危险的预设。请在 Splatoon 的常规设置中启用对应选项；6.5 以前的旧预设通常没有此标记。");
        }

        internal static void WriteRequest()
        {
            var array = Svc.PluginInterface.GetOrCreateData<HashSet<string>>("Splatoon.UnsafeElementRequesters", () => []);
            array.Add(Svc.PluginInterface.InternalName);
        }

        internal static bool IsUnsafe()
        {
            if(!P.config.SplatoonUnsafePixel) return false;
            if (Svc.PluginInterface.TryGetData<bool[]>("Splatoon.IsInUnsafeZone", out var data)) return data[0];
            return false;
        }
    }
}
