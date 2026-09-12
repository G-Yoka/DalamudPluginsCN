using CrescentCompass.Configuration;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrescentCompass.Windows;

public sealed class MainWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly Action save;
    private readonly Action openConfiguration;
    private readonly Action openCeWatch;
    private readonly Action openFateWatch;
    private readonly Action openMap;
    private readonly TreasureTracker tracker;

    public MainWindow(PluginConfiguration configuration, TreasureTracker tracker, Action save, Action openConfiguration,
        Action? openCeWatch = null, Action? openFateWatch = null, Action? openMap = null)
        : base("新月罗盘###CrescentCompass-Main")
    {
        this.configuration = configuration;
        this.tracker = tracker;
        this.save = save;
        this.openConfiguration = openConfiguration;
        this.openCeWatch = openCeWatch ?? (() => { });
        this.openFateWatch = openFateWatch ?? (() => { });
        this.openMap = openMap ?? (() => { });
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(340f, 150f),
            MaximumSize = new(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        var colors = UiTheme.PushContentStyle(configuration.ComfortableUiDensity);
        ImGui.TextColored(UiTheme.Gold, $"{tracker.AreaName} · 第 {tracker.Session.Round} 处财宝");
        ImGui.Separator();
        ImGui.TextWrapped(tracker.Status);
        ImGui.TextDisabled("剩余候选");
        ImGui.SetWindowFontScale(1.35f);
        ImGui.TextColored(UiTheme.Cyan, tracker.Session.Candidates.Count.ToString("D2"));
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine();
        ImGui.TextDisabled($"已采纳 {tracker.Session.AcceptedHints.Count} 条提示");
        if (tracker.Session.AcceptedHints.LastOrDefault() is { SourceText: not null } hint)
            ImGui.TextDisabled($"最新提示：{hint.SourceText}");
        if (tracker.FocusedCandidate is { } candidate)
            ImGui.TextUnformatted($"当前关注：候选 #{tracker.GetCandidateNumber(candidate):D2}  X {candidate.Position.X:F1}  Z {candidate.Position.Z:F1}");
        if (tracker.ConfirmedTreasure is { } treasure)
            ImGui.TextColored(UiTheme.Green,
                $"已关联宝箱：X {treasure.Position.X:F1}  Z {treasure.Position.Z:F1}");
        ImGui.Spacing();
        var paused = configuration.Paused;
        if (ImGui.Checkbox("暂停运行逻辑", ref paused))
        {
            configuration.Paused = paused;
            save();
        }
        ImGui.SameLine();
        if (ImGui.Button("下一个")) tracker.FocusNext();
        ImGui.SameLine();
        if (ImGui.Button("撤销")) tracker.Undo();
        ImGui.SameLine();
        if (ImGui.Button("重新开始")) tracker.Reset();
        ImGui.Spacing();
        if (ImGui.Button("设置")) openConfiguration();
        ImGui.SameLine();
        if (ImGui.Button("CE 速查")) openCeWatch();
        ImGui.SameLine();
        if (ImGui.Button("FATE 速查")) openFateWatch();
        ImGui.SameLine();
        if (ImGui.Button("地图")) openMap();
        UiTheme.PopContentStyle(colors);
    }

    public override void PreDraw() => ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Panel);

    public override void PostDraw() => ImGui.PopStyleColor();
}
