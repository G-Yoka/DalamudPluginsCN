using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CrescentCompass.Windows;

internal static class UiTheme
{
    public static readonly Vector4 Panel = Rgba(32, 38, 51);
    public static readonly Vector4 PanelRaised = Rgba(41, 50, 67);
    public static readonly Vector4 Text = Rgba(232, 237, 244);
    public static readonly Vector4 Muted = Rgba(169, 179, 195);
    public static readonly Vector4 Gold = Rgba(255, 183, 40);
    public static readonly Vector4 Cyan = Rgba(90, 214, 230);
    public static readonly Vector4 Green = Rgba(117, 216, 88);
    public static readonly Vector4 Error = Rgba(240, 128, 112);

    public static int PushContentStyle(bool comfortable = false)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Muted);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel with { W = 0.72f });
        ImGui.PushStyleColor(ImGuiCol.FrameBg, PanelRaised with { W = 0.82f });
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Lighten(PanelRaised, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Lighten(PanelRaised, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.Button, PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(PanelRaised, 0.06f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Gold with { W = 0.48f });
        ImGui.PushStyleColor(ImGuiCol.Header, PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Lighten(PanelRaised, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Gold with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Cyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Cyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Gold);
        ImGui.PushStyleColor(ImGuiCol.Separator, Muted with { W = 0.28f });
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, comfortable ? new Vector2(10f, 8f) : new Vector2(8f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, comfortable ? new Vector2(9f, 6f) : new Vector2(7f, 4f));
        return 16;
    }

    public static void PopContentStyle(int colorCount)
    {
        ImGui.PopStyleVar(5);
        ImGui.PopStyleColor(colorCount);
    }

    public static void SectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Cyan, title);
        ImGui.Separator();
    }

    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(360f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static Vector4 Rgba(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    private static Vector4 Lighten(Vector4 color, float amount) => new(
        Math.Clamp(color.X + amount, 0f, 1f),
        Math.Clamp(color.Y + amount, 0f, 1f),
        Math.Clamp(color.Z + amount, 0f, 1f),
        color.W);
}
