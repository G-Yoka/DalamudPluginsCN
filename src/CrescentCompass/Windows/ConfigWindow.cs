using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace CrescentCompass.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly string[] Pages = ["外观", "地图标记", "场景标注", "宝箱", "寻宝", "事件提醒", "导航", "高级诊断"];
    private readonly PluginConfiguration configuration;
    private readonly Action save;
    private readonly Action applyOverlayVisibility;
    private readonly Action applyDetailsVisibility;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly NavigationService navigationService;
    private readonly TreasureTracker treasureTracker;
    private readonly Action previewProminentBanner;
    private readonly Action testInGameNotificationSound;
    private readonly Action testWindowsNotification;
    private readonly Func<string> windowsNotificationTestStatus;
    private string search = string.Empty;
    private int selectedPage;

    public ConfigWindow(PluginConfiguration configuration, Action save,
        Action applyOverlayVisibility, Action applyDetailsVisibility,
        IDalamudPluginInterface pluginInterface, NavigationService navigationService,
        TreasureTracker treasureTracker, Action previewProminentBanner,
        Action testInGameNotificationSound, Action testWindowsNotification,
        Func<string> windowsNotificationTestStatus)
        : base("新月罗盘设置###CrescentCompass-Config")
    {
        this.configuration = configuration;
        this.save = save;
        this.applyOverlayVisibility = applyOverlayVisibility;
        this.applyDetailsVisibility = applyDetailsVisibility;
        this.pluginInterface = pluginInterface;
        this.navigationService = navigationService;
        this.treasureTracker = treasureTracker;
        this.previewProminentBanner = previewProminentBanner;
        this.testInGameNotificationSound = testInGameNotificationSound;
        this.testWindowsNotification = testWindowsNotification;
        this.windowsNotificationTestStatus = windowsNotificationTestStatus;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(620f, 430f),
            MaximumSize = new(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        var colors = UiTheme.PushContentStyle(configuration.ComfortableUiDensity);
        try
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("###CrescentCompass-SettingSearch", "搜索设置", ref search, 64);
            ImGui.Spacing();
            var navigationWidth = 132f * ImGuiHelpers.GlobalScale;
            ImGui.BeginChild("###CrescentCompass-ConfigPages", new Vector2(navigationWidth, 0f), true);
            for (var index = 0; index < Pages.Length; index++)
            {
                if (!MatchesSearch(Pages[index]) && search.Length > 0) continue;
                if (ImGui.Selectable(Pages[index], selectedPage == index)) selectedPage = index;
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("###CrescentCompass-ConfigContent", Vector2.Zero, true);
            ImGui.TextColored(UiTheme.Gold, Pages[selectedPage]);
            ImGui.TextDisabled(PageDescription(selectedPage));
            ImGui.Separator();
            switch (selectedPage)
            {
                case 0: DrawAppearance(); break;
                case 1: DrawMapMarkers(); break;
                case 2: DrawSceneLabels(); break;
                case 3: DrawChests(); break;
                case 4: DrawTreasure(); break;
                case 5: DrawEvents(); break;
                case 6: DrawNavigation(); break;
                case 7: DrawDiagnostics(); break;
            }
            ImGui.EndChild();
        }
        finally { UiTheme.PopContentStyle(colors); }
    }

    public override void PreDraw() => ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Panel);

    public override void PostDraw() => ImGui.PopStyleColor();

    private void DrawAppearance()
    {
        DrawBoolean("显示寻宝地图浮窗", configuration.ShowOverlay, value =>
        {
            configuration.ShowOverlay = value;
            applyOverlayVisibility();
        });
        DrawBoolean("默认展开地图详情栏", configuration.MapDetailsExpanded, value =>
        {
            configuration.MapDetailsExpanded = value;
            applyDetailsVisibility();
        });
        DrawBoolean("减少动态效果", configuration.ReduceMotion, value => configuration.ReduceMotion = value);
        SliderPercent("浮窗透明度", configuration.WindowOpacity, value => configuration.WindowOpacity = value, 25, 100);
        SliderPercent("地图底图透明度", configuration.MapTextureOpacity, value => configuration.MapTextureOpacity = value, 25, 100);
        SliderPercent("地图标记透明度", configuration.MapMarkerOpacity, value => configuration.MapMarkerOpacity = value, 25, 100);
    }

    private void DrawMapMarkers()
    {
        DrawScale("传送水晶", configuration.AetheryteIconScale, value => configuration.AetheryteIconScale = value, 24f);
        DrawScale("CE", configuration.CeIconScale, value => configuration.CeIconScale = value, 30f);
        DrawScale("FATE", configuration.FateIconScale, value => configuration.FateIconScale = value, 26f);
        DrawScale("魔法罐", configuration.MagicPotIconScale, value => configuration.MagicPotIconScale = value, 28f);
        DrawScale("魔法罐预告", configuration.ForecastIconScale, value => configuration.ForecastIconScale = value, 32f);
        DrawScale("其他地图地标", configuration.LandmarkIconScale, value => configuration.LandmarkIconScale = value, 32f);
        DrawScale("普通候选", configuration.CandidateIconScale, value => configuration.CandidateIconScale = value, 13f);
        DrawScale("关注候选", configuration.FocusedCandidateIconScale, value => configuration.FocusedCandidateIconScale = value, 18f);
        DrawScale("宝箱静态点位", configuration.FieldTreasurePointIconScale,
            value => configuration.FieldTreasurePointIconScale = value, 10f);
        DrawScale("已确认宝箱", configuration.ConfirmedFieldTreasureIconScale,
            value => configuration.ConfirmedFieldTreasureIconScale = value, 16f);
        DrawScale("队友标记", configuration.PartyMemberIconScale,
            value => configuration.PartyMemberIconScale = value, 18f);
        DrawScale("悬停放大", configuration.MapIconHoverScale, value => configuration.MapIconHoverScale = value, 1f, 1f, 2.5f);
        DrawMarkerPreview();
        if (ImGui.Button("恢复本页默认值"))
        {
            configuration.AetheryteIconScale = 1.25f;
            configuration.CeIconScale = configuration.FateIconScale = 1f;
            configuration.MagicPotIconScale = configuration.ForecastIconScale = configuration.LandmarkIconScale = 1f;
            configuration.CandidateIconScale = configuration.FocusedCandidateIconScale = 0.8f;
            configuration.FieldTreasurePointIconScale = configuration.ConfirmedFieldTreasureIconScale = 0.5f;
            configuration.PartyMemberIconScale = 0.6f;
            configuration.MapIconHoverScale = 1.15f;
            save();
        }
    }

    private void DrawMarkerPreview()
    {
        UiTheme.SectionTitle("实时尺寸预览");
        var height = 92f * ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.InvisibleButton("###CrescentCompass-MarkerPreview", new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(UiTheme.PanelRaised), 8f);
        var samples = new (string Name, float Size, uint Color)[]
        {
            ("水晶", 24f * configuration.AetheryteIconScale, 0xFFE6D65A),
            ("CE", 30f * configuration.CeIconScale, 0xFF28B7FF),
            ("FATE", 26f * configuration.FateIconScale, 0xFF7080F0),
            ("候选", 13f * configuration.CandidateIconScale, 0xFFE6D65A),
            ("关注", 18f * configuration.FocusedCandidateIconScale, 0xFF28B7FF)
        };
        var step = width / samples.Length;
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var center = origin + new Vector2(step * (index + 0.5f), 36f * ImGuiHelpers.GlobalScale);
            var radius = MathF.Max(3f, sample.Size * ImGuiHelpers.GlobalScale / 2f);
            draw.AddCircleFilled(center, radius, sample.Color, 24);
            draw.AddCircle(center, radius, 0xFFFFFFFF, 24, 1.25f);
            var textSize = ImGui.CalcTextSize(sample.Name);
            draw.AddText(center + new Vector2(-textSize.X / 2f, radius + 7f), 0xFFDDE5EF, sample.Name);
        }
    }

    private void DrawSceneLabels()
    {
        DrawBoolean("显示候选场景标注", configuration.ShowCandidateIndicators, value => configuration.ShowCandidateIndicators = value);
        DrawBoolean("显示罐子发现的宝藏", configuration.ShowTreasureIndicators, value => configuration.ShowTreasureIndicators = value);
        DrawBoolean("显示 FATE／CE／魔法罐", configuration.ShowEventSceneIndicators, value => configuration.ShowEventSceneIndicators = value);
        DrawBoolean("战斗中隐藏场景标注", configuration.HideIndicatorsInCombat, value => configuration.HideIndicatorsInCombat = value);
        SliderInt("场景候选上限", configuration.MaxSceneCandidates, value => configuration.MaxSceneCandidates = value, 1, 12);
        SliderFloat("场景标注范围", configuration.IndicatorRadius, value => configuration.IndicatorRadius = value, 50f, 1000f, "%.0fm");
    }

    private void DrawChests()
    {
        DrawBoolean("地图显示铜／银宝箱静态点位", configuration.ShowFieldTreasureMapMarkers,
            value => configuration.ShowFieldTreasureMapMarkers = value);
        DrawBoolean("地图显示已确认的普通宝箱", configuration.ShowConfirmedFieldTreasureMapMarkers,
            value => configuration.ShowConfirmedFieldTreasureMapMarkers = value);
        DrawBoolean("场景显示已刷新的普通宝箱", configuration.ShowFieldTreasureIndicators,
            value => configuration.ShowFieldTreasureIndicators = value);
        SliderFloat("普通宝箱显示距离", configuration.FieldTreasureIndicatorRadius,
            value => configuration.FieldTreasureIndicatorRadius = value, 20f, 1000f, "%.0fm");
        DrawBoolean("进入区域后自动调查一次", configuration.AutoSurveyFieldTreasureCounts,
            value => configuration.AutoSurveyFieldTreasureCounts = value);
    }

    private void DrawTreasure()
    {
        DrawBoolean("显示地图", configuration.ShowMap, value => configuration.ShowMap = value);
        DrawBoolean("暂停寻宝处理", configuration.Paused, value => configuration.Paused = value);
        UiTheme.SectionTitle("候选点校准");
        DrawBoolean("自动校准魔法罐候选点", configuration.AutoCalibratePotCandidates,
            value => configuration.AutoCalibratePotCandidates = value);
        ImGui.TextDisabled($"已校准 {treasureTracker.CandidateCalibrationCount} 个候选点。只有匹配明确时才会保存。");
        ImGui.TextDisabled(treasureTracker.CandidateCalibrationStatus);
        if (treasureTracker.CandidateCalibrationCount == 0) ImGui.BeginDisabled();
        if (ImGui.Button("复制校准记录"))
            ImGui.SetClipboardText(treasureTracker.ExportCandidateCalibrations());
        ImGui.SameLine();
        if (ImGui.Button("清除校准记录")) treasureTracker.ResetCandidateCalibrations();
        if (treasureTracker.CandidateCalibrationCount == 0) ImGui.EndDisabled();
    }

    private void DrawEvents()
    {
        DrawBoolean("收藏的 CE／FATE 出现时提醒", configuration.NotifyWatchedCe, value => configuration.NotifyWatchedCe = value);
        DrawBoolean("游戏失焦时使用 Windows 横幅", configuration.ShowWindowsEventNotifications,
            value => configuration.ShowWindowsEventNotifications = value);
        ImGui.SameLine();
        if (ImGui.Button("测试系统通知")) testWindowsNotification();
        ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.Muted);
        ImGui.TextWrapped(windowsNotificationTestStatus());
        ImGui.PopStyleColor();
        DrawBoolean("播放游戏内提示音", configuration.WatchedCeSound, value => configuration.WatchedCeSound = value);
        ImGui.SameLine();
        if (!configuration.WatchedCeSound) ImGui.BeginDisabled();
        if (ImGui.Button("试听提示音")) testInGameNotificationSound();
        DrawEventNotificationSound();
        if (!configuration.WatchedCeSound) ImGui.EndDisabled();
        DrawBoolean("进入新实例时提醒已出现的收藏事件", configuration.NotifyCeOnEntry, value => configuration.NotifyCeOnEntry = value);
        ImGui.Spacing();
        ImGui.TextDisabled($"当前已关注 {configuration.WatchedCeIds.Count} 个 CE、{configuration.WatchedFateIds.Count} 个 FATE。收藏列表可在对应速查窗口管理。");

        UiTheme.SectionTitle("醒目横幅");
        DrawBoolean("显示醒目的游戏内横幅", configuration.ShowProminentInGameEventNotifications,
            value => configuration.ShowProminentInGameEventNotifications = value);
        if (!configuration.ShowProminentInGameEventNotifications) ImGui.BeginDisabled();
        DrawBannerPosition();
        DrawBannerDetail();
        SliderFloat("显示时长", configuration.ProminentBannerDurationSeconds,
            value => configuration.ProminentBannerDurationSeconds = value, 3f, 30f, "%.0f秒");
        SliderFloat("横幅宽度", configuration.ProminentBannerWidth,
            value => configuration.ProminentBannerWidth = value, 420f, 900f, "%.0fpx");
        SliderPercent("横幅透明度", configuration.ProminentBannerOpacity,
            value => configuration.ProminentBannerOpacity = value, 50, 100);
        DrawBoolean("鼠标悬停时暂停计时", configuration.PauseProminentBannerOnHover,
            value => configuration.PauseProminentBannerOnHover = value);
        DrawBoolean("显示“前往”按钮", configuration.ShowProminentBannerNavigateButton,
            value => configuration.ShowProminentBannerNavigateButton = value);
        if (configuration.ProminentBannerPosition == EventBannerPosition.Custom)
            ImGui.TextDisabled("横幅出现后可直接拖动；松开鼠标时保存位置。");
        if (ImGui.Button("测试横幅")) previewProminentBanner();
        ImGui.SameLine();
        if (ImGui.Button("恢复横幅默认设置"))
        {
            configuration.ProminentBannerPosition = EventBannerPosition.TopCenter;
            configuration.ProminentBannerDetail = EventBannerDetail.Standard;
            configuration.ProminentBannerDurationSeconds = 8f;
            configuration.ProminentBannerWidth = 620f;
            configuration.ProminentBannerOpacity = 0.6f;
            configuration.ProminentBannerCustomX = 0.5f;
            configuration.ProminentBannerCustomY = 0.04f;
            configuration.PauseProminentBannerOnHover = true;
            configuration.ShowProminentBannerNavigateButton = true;
            save();
        }
        if (!configuration.ShowProminentInGameEventNotifications) ImGui.EndDisabled();
    }

    private void DrawEventNotificationSound()
    {
        var current = Math.Clamp(configuration.EventNotificationSoundEffect, 1, 16);
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("提示音", $"<se.{current}>"))
        {
            for (var sound = 1; sound <= 16; sound++)
            {
                if (!ImGui.Selectable($"<se.{sound}>", current == sound)) continue;
                configuration.EventNotificationSoundEffect = sound;
                save();
                testInGameNotificationSound();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawBannerPosition()
    {
        var labels = new[] { "顶部居中", "左上角", "右上角", "自定义位置" };
        var current = (int)configuration.ProminentBannerPosition;
        ImGui.SetNextItemWidth(300f);
        if (!ImGui.BeginCombo("横幅位置", labels[current])) return;
        for (var index = 0; index < labels.Length; index++)
        {
            if (ImGui.Selectable(labels[index], current == index))
            {
                configuration.ProminentBannerPosition = (EventBannerPosition)index;
                save();
            }
            if (current == index) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private void DrawBannerDetail()
    {
        var labels = new[] { "简洁", "标准", "详细" };
        var current = (int)configuration.ProminentBannerDetail;
        ImGui.SetNextItemWidth(300f);
        if (!ImGui.BeginCombo("内容详细程度", labels[current])) return;
        for (var index = 0; index < labels.Length; index++)
        {
            if (ImGui.Selectable(labels[index], current == index))
            {
                configuration.ProminentBannerDetail = (EventBannerDetail)index;
                save();
            }
            if (current == index) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private void DrawNavigation()
    {
        DrawBoolean("WASD／Esc 取消自动导航", configuration.InterruptNavigationOnMovementInput, value => configuration.InterruptNavigationOnMovementInput = value);
        DrawBoolean("显示路线图层", configuration.ShowMapRouteLayer, value => configuration.ShowMapRouteLayer = value);
        DrawBoolean("显示怪物警戒范围", configuration.ShowMonsterAggroRanges, value => configuration.ShowMonsterAggroRanges = value);
        DrawBoolean("自动导航避开怪物", configuration.AvoidMonsterAggroRanges, value => configuration.AvoidMonsterAggroRanges = value);
        DrawBoolean("事件目标使用随机落点", configuration.RandomizeEventNavigationDestination,
            value => configuration.RandomizeEventNavigationDestination = value);
        if (!configuration.RandomizeEventNavigationDestination) ImGui.BeginDisabled();
        SliderFloat("随机落点范围", configuration.EventNavigationRandomRadius,
            value => configuration.EventNavigationRandomRadius = value, 2f, 15f, "%.0fm");
        if (!configuration.RandomizeEventNavigationDestination) ImGui.EndDisabled();
        SliderFloat("近距离直接导航阈值", configuration.DirectNavigationDistance, value => configuration.DirectNavigationDistance = value, 0f, 300f, "%.0fm");
        ImGui.TextDisabled("仅在路线比较失败或超时时作为回退规则。");
        SliderFloat("传送路线最少节省时间", configuration.MinimumTeleportSavingSeconds,
            value => configuration.MinimumTeleportSavingSeconds = value, 0f, 60f, "%.0f秒");
        UiTheme.SectionTitle("北部警戒范围");
        SliderFloat("警戒边缘距离", configuration.AggroEdgeRange, value => configuration.AggroEdgeRange = value, 4f, 20f, "%.1fm");
        SliderFloat("安全余量", configuration.AggroSafetyMargin, value => configuration.AggroSafetyMargin = value, 0f, 6f, "%.1fm");
        SliderFloat("扫描范围", configuration.AggroScanRange, value => configuration.AggroScanRange = value, 20f, 150f, "%.0fm");
        SliderFloat("垂直容差", configuration.AggroVerticalTolerance, value => configuration.AggroVerticalTolerance = value, 1f, 20f, "%.1fm");
    }

    private void DrawDiagnostics()
    {
        UiTheme.SectionTitle("插件依赖");
        DrawVnavmeshStatus();
        UiTheme.SectionTitle("本机路线耗时");
        ImGui.TextDisabled($"亚返回 {configuration.AverageDemiReturnSeconds:F1}秒 · 样本 {configuration.DemiReturnTimingSamples}");
        ImGui.TextDisabled($"水晶传送 {configuration.AverageCrystalTransferSeconds:F1}秒 · 样本 {configuration.CrystalTransferTimingSamples}");
        ImGui.TextDisabled($"上坐骑 {configuration.AverageMountSeconds:F1}秒 · 样本 {configuration.MountTimingSamples}");
        ImGui.TextDisabled($"下坐骑 {configuration.AverageDismountSeconds:F1}秒 · 样本 {configuration.DismountTimingSamples}");
        UiTheme.SectionTitle("警戒校准");
        DrawBoolean("显示警戒调试信息", configuration.ShowAggroDebug, value => configuration.ShowAggroDebug = value);
        DrawBoolean("自动校准警戒范围", configuration.AutoCalibrateAggroRanges, value => configuration.AutoCalibrateAggroRanges = value);
    }

    private void DrawVnavmeshStatus()
    {
        var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(item =>
            string.Equals(item.InternalName, "vnavmesh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, "vnavmesh", StringComparison.OrdinalIgnoreCase));
        var loaded = plugin?.IsLoaded == true;
        var ready = loaded && navigationService.IsVnavmeshReady;
        var (runtime, runtimeColor) = plugin switch
        {
            null => ("不可用", UiTheme.Error),
            _ when !loaded => ("未启用", UiTheme.Gold),
            _ when !ready => ("IPC 尚未就绪", UiTheme.Gold),
            _ => ("正常", UiTheme.Cyan)
        };

        ImGui.TextColored(UiTheme.Text, "vnavmesh");
        ImGui.TextUnformatted("安装");
        ImGui.SameLine();
        ImGui.TextColored(plugin == null ? UiTheme.Error : UiTheme.Cyan, plugin == null ? "未安装" : "已安装");
        ImGui.TextUnformatted("运行");
        ImGui.SameLine();
        ImGui.TextColored(runtimeColor, runtime);
        ImGui.TextUnformatted("版本");
        ImGui.SameLine();
        ImGui.TextDisabled(plugin == null ? "—" : $"{plugin.Version}{(plugin.IsOutdated ? " · 有可用更新" : string.Empty)}");
    }

    private void DrawScale(string label, float current, Action<float> apply, float baseSize, float minimum = 0.5f, float maximum = 3f)
    {
        var value = current;
        ImGui.SetNextItemWidth(MathF.Min(300f, MathF.Max(120f, ImGui.GetContentRegionAvail().X - 110f)));
        if (ImGui.SliderFloat($"{label}##scale", ref value, minimum, maximum, "%.2fx", ImGuiSliderFlags.AlwaysClamp))
        {
            apply(value);
            save();
        }
        if (baseSize <= 1f) return;
        ImGui.SameLine();
        ImGui.TextDisabled($"约 {baseSize * value:F0}px");
    }

    private void SliderPercent(string label, float current, Action<float> apply, int minimum, int maximum)
    {
        var percent = (int)MathF.Round(current * 100f);
        ImGui.SetNextItemWidth(300f);
        if (!ImGui.SliderInt(label, ref percent, minimum, maximum, "%d%%", ImGuiSliderFlags.AlwaysClamp)) return;
        apply(percent / 100f);
        save();
    }

    private void SliderInt(string label, int current, Action<int> apply, int minimum, int maximum)
    {
        var value = current;
        ImGui.SetNextItemWidth(300f);
        if (!ImGui.SliderInt(label, ref value, minimum, maximum, "%d", ImGuiSliderFlags.AlwaysClamp)) return;
        apply(value);
        save();
    }

    private void SliderFloat(string label, float current, Action<float> apply, float minimum, float maximum, string format)
    {
        var value = current;
        ImGui.SetNextItemWidth(300f);
        if (!ImGui.SliderFloat(label, ref value, minimum, maximum, format, ImGuiSliderFlags.AlwaysClamp)) return;
        apply(value);
        save();
    }

    private void DrawBoolean(string label, bool value, Action<bool> apply)
    {
        if (!ImGui.Checkbox(label, ref value)) return;
        apply(value);
        save();
    }

    private bool MatchesSearch(string page) => search.Trim().Length == 0 ||
        page.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ||
        PageDescription(Array.IndexOf(Pages, page)).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string PageDescription(int page) => page switch
    {
        0 => "窗口密度、透明度与动画偏好",
        1 => "分别校准水晶、事件、候选和地标",
        2 => "游戏场景中的候选与宝箱标签",
        3 => "铜银宝箱点位、场景标注与数量调查",
        4 => "寻宝显示与暂停控制",
        5 => "CE／FATE 收藏和提醒行为",
        6 => "vnavmesh、路线与怪物避让",
        _ => "开发诊断与校准工具"
    };
}
