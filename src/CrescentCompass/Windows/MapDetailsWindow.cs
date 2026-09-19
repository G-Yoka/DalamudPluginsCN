using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace CrescentCompass.Windows;

public sealed class MapDetailsWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly TreasureTracker tracker;
    private readonly OccultEventTracker eventTracker;
    private readonly EventAutomationService eventAutomationService;
    private readonly NavigationService navigationService;
    private readonly TreasureSurveyService treasureSurveyService;
    private readonly TreasureMapWindow mapWindow;
    private readonly Action openCeWatch;
    private readonly Action openFateWatch;
    private readonly Action openAutomationTargets;
    private readonly Action save;
    private int page;

    public MapDetailsWindow(PluginConfiguration configuration, TreasureTracker tracker,
        OccultEventTracker eventTracker, EventAutomationService eventAutomationService,
        NavigationService navigationService,
        TreasureSurveyService treasureSurveyService, TreasureMapWindow mapWindow,
        Action openCeWatch, Action openFateWatch, Action openAutomationTargets, Action save)
        : base("新月罗盘详情###CrescentCompass-MapDetails")
    {
        this.configuration = configuration;
        this.tracker = tracker;
        this.eventTracker = eventTracker;
        this.eventAutomationService = eventAutomationService;
        this.navigationService = navigationService;
        this.treasureSurveyService = treasureSurveyService;
        this.mapWindow = mapWindow;
        this.openCeWatch = openCeWatch;
        this.openFateWatch = openFateWatch;
        this.openAutomationTargets = openAutomationTargets;
        this.save = save;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(270f, 320f),
            MaximumSize = new(460f, float.MaxValue)
        };
    }

    public override bool DrawConditions() => tracker.IsSupportedTerritory && mapWindow.IsOpen;

    public override void PreDraw()
    {
        BgAlpha = configuration.WindowOpacity;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Panel);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f * ImGuiHelpers.GlobalScale);
        var viewport = ImGui.GetMainViewport();
        var scale = ImGuiHelpers.GlobalScale;
        var gap = 8f * scale;
        var width = 300f * scale;
        var height = Math.Clamp(mapWindow.ScreenSize.Y, 320f * scale, viewport.WorkSize.Y);
        var right = mapWindow.ScreenPosition.X + mapWindow.ScreenSize.X + gap;
        var left = mapWindow.ScreenPosition.X - width - gap;
        float x;
        float y;
        if (right + width <= viewport.WorkPos.X + viewport.WorkSize.X)
        {
            x = right;
            y = mapWindow.ScreenPosition.Y;
        }
        else if (left >= viewport.WorkPos.X)
        {
            x = left;
            y = mapWindow.ScreenPosition.Y;
        }
        else
        {
            x = Math.Clamp(mapWindow.ScreenPosition.X, viewport.WorkPos.X,
                viewport.WorkPos.X + viewport.WorkSize.X - width);
            y = mapWindow.ScreenPosition.Y + mapWindow.ScreenSize.Y + gap;
            if (y + height > viewport.WorkPos.Y + viewport.WorkSize.Y)
                y = Math.Max(viewport.WorkPos.Y, mapWindow.ScreenPosition.Y - height - gap);
        }
        Position = new Vector2(x, Math.Clamp(y, viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - height));
        PositionCondition = ImGuiCond.Always;
        Size = new Vector2(width, height);
        SizeCondition = ImGuiCond.Always;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    public override void OnClose()
    {
        configuration.MapDetailsExpanded = false;
        save();
    }

    public override void Draw()
    {
        var colors = UiTheme.PushContentStyle(configuration.ComfortableUiDensity);
        try
        {
            DrawTabs();
            ImGui.Separator();
            switch (page)
            {
                case 0: DrawEvents(); break;
                case 1: DrawAutomation(); break;
                case 2: DrawChests(); break;
                case 3: DrawTreasure(); break;
                default: DrawRoute(); break;
            }
        }
        finally { UiTheme.PopContentStyle(colors); }
    }

    private void DrawTabs()
    {
        var available = ImGui.GetContentRegionAvail().X;
        var width = MathF.Max(40f, (available - ImGui.GetStyle().ItemSpacing.X * 4f) / 5f);
        if (DrawTab("事件", page == 0, width)) page = 0;
        ImGui.SameLine();
        if (DrawTab("自动", page == 1, width)) page = 1;
        ImGui.SameLine();
        if (DrawTab("宝箱", page == 2, width)) page = 2;
        ImGui.SameLine();
        if (DrawTab("寻宝", page == 3, width)) page = 3;
        ImGui.SameLine();
        if (DrawTab("路线", page == 4, width)) page = 4;
    }

    private void DrawAutomation()
    {
        ImGui.TextColored(
            eventAutomationService.Stage == EventAutomationStage.Suspended ? UiTheme.Error : UiTheme.Cyan,
            StageLabel(eventAutomationService.Stage));
        ImGui.TextWrapped(eventAutomationService.Status);
        ImGui.TextDisabled($"当前目标 · {eventAutomationService.TargetLabel}");

        if (configuration.EnableEventAutomation)
        {
            if (ImGui.Button("停止自动事件", new Vector2(-1f, 0f)))
                eventAutomationService.Stop();
            if (eventAutomationService.Stage == EventAutomationStage.Suspended &&
                ImGui.Button("恢复自动事件", new Vector2(-1f, 0f)))
                eventAutomationService.Resume();
        }
        else if (ImGui.Button("启动自动事件", new Vector2(-1f, 0f)))
        {
            configuration.EnableEventAutomation = true;
            save();
            eventAutomationService.Resume();
        }

        UiTheme.SectionTitle("等待点");
        ImGui.TextUnformatted(eventAutomationService.WaitingPointLabel);
        ImGui.TextDisabled(eventAutomationService.UsesDefaultWaitingPoint
            ? "当前使用总部大水晶旁的默认等待点"
            : "当前使用自定义等待点");
        if (ImGui.Button("记录当前位置")) eventAutomationService.RecordCurrentWaitingPoint();
        ImGui.SameLine();
        if (ImGui.Button("恢复默认大水晶")) eventAutomationService.ClearCurrentWaitingPoint();

        UiTheme.SectionTitle("参与范围");
        ImGui.TextDisabled($"已选择 {configuration.AutomatedCeIds.Count} 个 CE、{configuration.AutomatedFateIds.Count} 个 FATE");
        if (ImGui.Button("打开自动参与列表", new Vector2(-1f, 0f))) openAutomationTargets();
    }

    private void DrawChests()
    {
        UiTheme.SectionTitle("宝箱状态");
        ImGui.TextUnformatted($"已确认存在 · {tracker.FieldTreasures.Count}");
        ImGui.Spacing();
        ImGui.TextDisabled("当前区域剩余");
        if (treasureSurveyService.BronzeCount is { } bronze && treasureSurveyService.SilverCount is { } silver)
        {
            ImGui.TextColored(new Vector4(0.88f, 0.57f, 0.27f, 1f), $"铜宝箱  {bronze} / 30");
            ImGui.TextColored(new Vector4(0.84f, 0.90f, 0.96f, 1f), $"银宝箱  {silver} / 8");
            if (treasureSurveyService.LastSurveyAt is { } surveyedAt)
            {
                var elapsed = Math.Max(0, (int)(DateTimeOffset.Now - surveyedAt).TotalSeconds);
                ImGui.TextDisabled($"上次调查 · {elapsed}秒前");
            }
        }
        else
        {
            var confirmedBronze = tracker.FieldTreasures.Count(item => item.Kind == FieldTreasureKind.Bronze);
            var confirmedSilver = tracker.FieldTreasures.Count(item => item.Kind == FieldTreasureKind.Silver);
            var confirmedUnknown = tracker.FieldTreasures.Count(item => item.Kind == FieldTreasureKind.Unknown);
            ImGui.TextColored(new Vector4(0.88f, 0.57f, 0.27f, 1f), $"铜宝箱  ≥ {confirmedBronze} / 30");
            ImGui.TextColored(new Vector4(0.84f, 0.90f, 0.96f, 1f), $"银宝箱  ≥ {confirmedSilver} / 8");
            if (confirmedUnknown > 0)
                ImGui.TextDisabled($"另有 {confirmedUnknown} 个已确认宝箱尚未分类");
        }
        ImGui.TextDisabled(treasureSurveyService.Status);
        if (treasureSurveyService.IsBusy) ImGui.BeginDisabled();
        if (ImGui.Button(treasureSurveyService.IsBusy ? "正在调查…" : "调查宝箱数量", new Vector2(-1f, 0f)))
            treasureSurveyService.RequestSurvey();
        if (treasureSurveyService.IsBusy) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.22f, 0.75f, 0.96f, 1f), "●");
        ImGui.SameLine();
        ImGui.TextDisabled("铜／银宝箱可能刷新位置");
        ImGui.TextColored(new Vector4(0.88f, 0.57f, 0.27f, 1f), "●");
        ImGui.SameLine();
        ImGui.TextDisabled("铜宝箱点位");
        ImGui.TextColored(new Vector4(0.84f, 0.90f, 0.96f, 1f), "●");
        ImGui.SameLine();
        ImGui.TextDisabled("银宝箱点位");
        ImGui.TextColored(UiTheme.Green, "◎");
        ImGui.SameLine();
        ImGui.TextDisabled("当前确认存在的宝箱");
    }

    private static bool DrawTab(string label, bool selected, float width)
    {
        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, UiTheme.Gold with { W = 0.56f });
        var clicked = ImGui.Button(label, new Vector2(width, 0f));
        if (selected) ImGui.PopStyleColor();
        return clicked;
    }

    private void DrawTreasure()
    {
        ImGui.TextDisabled("剩余候选");
        ImGui.SetWindowFontScale(1.45f);
        ImGui.TextColored(UiTheme.Cyan, tracker.Session.Candidates.Count.ToString("D2"));
        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped(tracker.Status);
        if (tracker.FocusedCandidate is { } candidate)
        {
            UiTheme.SectionTitle($"当前关注 #{tracker.GetCandidateNumber(candidate):D2}");
            if (TryGetDistance(candidate, out var distance))
                ImGui.TextUnformatted($"{distance:F0}m · X {candidate.Position.X:F1} · Z {candidate.Position.Z:F1}");
            else ImGui.TextDisabled($"X {candidate.Position.X:F1} · Z {candidate.Position.Z:F1}");
        }
        UiTheme.SectionTitle($"提示记录 · {tracker.Session.AcceptedHints.Count}");
        if (tracker.Session.AcceptedHints.Count == 0) ImGui.TextDisabled("收到罐子提示后将在这里显示记录。");
        foreach (var hint in tracker.Session.AcceptedHints)
            ImGui.BulletText($"{DirectionName(hint.Direction)} · {DistanceName(hint.DistanceBand)}");
        if (tracker.Session.ConflictingHint != null)
            ImGui.TextColored(UiTheme.Error, "最新提示与当前候选冲突，已保留上一组结果。");
        ImGui.Spacing();
        if (ImGui.Button("下一个候选")) tracker.FocusNext();
        ImGui.SameLine();
        if (ImGui.Button("撤销提示")) tracker.Undo();
        if (ImGui.Button("重置本轮")) tracker.Reset();
    }

    private void DrawEvents()
    {
        var events = eventTracker.ActiveEvents;
        ImGui.TextDisabled($"当前可见事件 · {events.Count}");
        foreach (var item in events.Take(10))
        {
            ImGui.PushID((int)item.DataId);
            ImGui.TextColored(item.Kind == OccultEventKind.CriticalEngagement ? UiTheme.Gold : UiTheme.Cyan,
                EventKindName(item.Kind));
            ImGui.SameLine();
            var nameWidth = ImGui.CalcTextSize(item.Name).X + ImGui.GetStyle().FramePadding.X * 2f;
            if (ImGui.Selectable($"{item.Name}##event", false, ImGuiSelectableFlags.None, new Vector2(nameWidth, 0f)))
                navigationService.NavigateToEvent(item.Position, $"{EventKindName(item.Kind)}：{item.Name}",
                    item.DataId, CustomRouteKind(item.Kind));
            if (!string.IsNullOrEmpty(item.RewardTag))
            {
                ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(RewardTagColor(item.RewardTag), item.RewardTag);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("半魂晶掉落颜色");
                    ImGui.EndTooltip();
                }
            }
            if (item.Kind == OccultEventKind.CriticalEngagement &&
                OccultEventRewardCatalog.TryGetSoulShard(tracker.TerritoryId, item.DataId, out var soulShard))
            {
                ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(SoulShardTagColor(soulShard.Tag), soulShard.Tag);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"灵魂碎晶：{soulShard.JobName}");
                    ImGui.EndTooltip();
                }
            }
            ImGui.TextDisabled(item.StateText);
            ImGui.PopID();
        }
        if (events.Count == 0) ImGui.TextDisabled("当前没有可显示的事件。预测记录会在有效时出现。");
        ImGui.Spacing();
        if (ImGui.Button("打开 Ｃ Ｅ 速查", new Vector2(-1f, 0f))) openCeWatch();
        if (ImGui.Button("打开 FATE 速查", new Vector2(-1f, 0f))) openFateWatch();
    }

    private static CustomNavigationRouteKind? CustomRouteKind(OccultEventKind kind) => kind switch
    {
        OccultEventKind.CriticalEngagement => CustomNavigationRouteKind.CriticalEngagement,
        OccultEventKind.Fate or OccultEventKind.MagicPot or OccultEventKind.MagicPotForecast =>
            CustomNavigationRouteKind.Fate,
        _ => null
    };

    private void DrawRoute()
    {
        ImGui.TextDisabled("导航状态");
        if (navigationService.IsNavigating)
        {
            ImGui.TextColored(UiTheme.Gold, "进行中");
            ImGui.TextWrapped(navigationService.Status);
            if (ImGui.Button("停止导航", new Vector2(-1f, 0f))) navigationService.Cancel();
        }
        else
        {
            ImGui.TextDisabled("未在导航");
            if (!string.IsNullOrWhiteSpace(navigationService.Status)) ImGui.TextWrapped(navigationService.Status);
        }

        UiTheme.SectionTitle("路线决策");
        ImGui.TextWrapped(navigationService.DecisionStatus);
        UiTheme.SectionTitle("路线图层");
        var showRoute = configuration.ShowMapRouteLayer;
        if (ImGui.Checkbox("在地图上显示实际路径", ref showRoute))
        {
            configuration.ShowMapRouteLayer = showRoute;
            save();
        }
        if (navigationService.DisplayPath.Count > 1)
            ImGui.TextDisabled($"当前路径 {navigationService.DisplayPath.Count} 个节点");
    }

    private static string EventKindName(OccultEventKind kind) => kind switch
    {
        OccultEventKind.CriticalEngagement => "CE", OccultEventKind.ForkTower => "两歧塔",
        OccultEventKind.MagicPot => "魔法罐", OccultEventKind.MagicPotForecast => "魔法罐预告", _ => "FATE"
    };

    private static string StageLabel(EventAutomationStage stage) => stage switch
    {
        EventAutomationStage.Disabled => "已停止",
        EventAutomationStage.Waiting => "等待事件",
        EventAutomationStage.Traveling => "前往事件",
        EventAutomationStage.AwaitingStart => "等待开始",
        EventAutomationStage.Participating => "参与中",
        EventAutomationStage.Settling => "结算等待",
        EventAutomationStage.Returning => "返回等待点",
        EventAutomationStage.TravelingToMagicPot => "前往魔法罐预告点",
        EventAutomationStage.WaitingForMagicPot => "等待魔法罐出现",
        EventAutomationStage.AwaitingMagicPotReward => "确认魔法罐奖励",
        EventAutomationStage.TreasureHunting => "自动寻找财宝",
        EventAutomationStage.Suspended => "已暂停",
        _ => stage.ToString()
    };

    private static Vector4 RewardTagColor(string tag) => tag switch
    {
        "[黄]" => new Vector4(0.96f, 0.83f, 0.37f, 1f),
        "[青]" => new Vector4(0.35f, 0.84f, 0.90f, 1f),
        "[碧]" => new Vector4(0.34f, 0.82f, 0.68f, 1f),
        "[绿]" => new Vector4(0.46f, 0.85f, 0.35f, 1f),
        "[橙]" => new Vector4(0.95f, 0.52f, 0.25f, 1f),
        "[紫]" => new Vector4(0.75f, 0.48f, 0.92f, 1f),
        _ => UiTheme.Text
    };

    private static Vector4 SoulShardTagColor(string tag) => tag switch
    {
        "[游]" => new Vector4(0.42f, 0.82f, 0.50f, 1f),
        "[狂]" => new Vector4(0.94f, 0.40f, 0.30f, 1f),
        "[预]" => new Vector4(0.48f, 0.76f, 1.00f, 1f),
        "[死]" => new Vector4(0.72f, 0.48f, 0.90f, 1f),
        "[青魔]" => new Vector4(0.32f, 0.66f, 0.96f, 1f),
        _ => UiTheme.Text
    };

    private static string DirectionName(PotDirection direction) => direction switch
    {
        PotDirection.North => "北", PotDirection.NorthEast => "东北", PotDirection.East => "东",
        PotDirection.SouthEast => "东南", PotDirection.South => "南", PotDirection.SouthWest => "西南",
        PotDirection.West => "西", _ => "西北"
    };

    private static string DistanceName(PotDistanceBand band) => band switch
    {
        PotDistanceBand.VeryNear => "很近", PotDistanceBand.Near => "不远",
        PotDistanceBand.Far => "稍远", _ => "很远"
    };

    private bool TryGetDistance(PotCandidate candidate, out float distance)
    {
        if (tracker.PlayerPosition is not { } player)
        {
            distance = 0f;
            return false;
        }
        var x = player.X - candidate.Position.X;
        var z = player.Z - candidate.Position.Z;
        distance = MathF.Sqrt(x * x + z * z);
        return true;
    }
}
