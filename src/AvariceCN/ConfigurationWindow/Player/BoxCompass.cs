using static Avarice.ConfigurationWindow.Ui;

namespace Avarice.ConfigurationWindow.Player;

internal static class BoxCompass
{
    internal static void Draw()
    {
        ImGui.PushID("compass");
        FeatureHeader("战术罗盘", ref P.currentProfile.CompassEnable,
            ref P.currentProfile.CompassCondition, "compass",
            "在玩家周围显示东、南、西、北方向。");
        if (P.currentProfile.CompassEnable)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
            ImGuiEx.EnumCombo("游戏字体", ref Prof.CompassFont);
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("字体缩放", ref Prof.CompassFontScale.ValidateRange(0, 100f), 0.5f, 20f);
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("与玩家的距离", ref Prof.CompassDistance.ValidateRange(0, float.MaxValue), 0.01f, 20f);
            ImGui.ColorEdit4("北方颜色", ref Prof.CompassColorN, ImGuiColorEditFlags.NoInputs);
            ImGui.SameLine();
            ImGui.ColorEdit4("其他方向颜色", ref Prof.CompassColor, ImGuiColorEditFlags.NoInputs);
            ImGui.Unindent();
        }
        ImGui.PopID();
    }
}
