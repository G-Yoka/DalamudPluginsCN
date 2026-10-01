using System.Numerics;
using CrescentCompass.Core;
using Dalamud.Bindings.ImGui;

namespace CrescentCompass.Windows;

internal static class PhantomDispellerUi
{
    public static void DrawTag(uint territoryId, uint eventId, float spacing = 4f)
    {
        if (!OccultEventRewardCatalog.TryGetPhantomDispeller(territoryId, eventId, out var kind)) return;

        ImGui.SameLine(0f, spacing);
        ImGui.TextColored(Color(kind), OccultEventRewardCatalog.PhantomDispellerTag(kind));
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"消幻晶 {Symbol(kind)}");
        ImGui.EndTooltip();
    }

    public static Vector4 Color(PhantomDispellerKind kind) => kind switch
    {
        PhantomDispellerKind.Alpha => new Vector4(0.35f, 0.84f, 0.96f, 1f),
        PhantomDispellerKind.Beta => new Vector4(0.76f, 0.50f, 0.96f, 1f),
        PhantomDispellerKind.Gamma => new Vector4(0.98f, 0.76f, 0.28f, 1f),
        _ => UiTheme.Text
    };

    private static string Symbol(PhantomDispellerKind kind) => kind switch
    {
        PhantomDispellerKind.Alpha => "α",
        PhantomDispellerKind.Beta => "β",
        PhantomDispellerKind.Gamma => "γ",
        _ => string.Empty
    };
}
