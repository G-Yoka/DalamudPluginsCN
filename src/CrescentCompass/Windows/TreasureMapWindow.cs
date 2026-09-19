using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using GameMap = Lumina.Excel.Sheets.Map;
using GameMapMarker = Lumina.Excel.Sheets.MapMarker;

namespace CrescentCompass.Windows;

public sealed class TreasureMapWindow : Window
{
    private const float WorldMinimum = -1024f;
    private const float WorldMaximum = 1024f;
    private readonly PluginConfiguration configuration;
    private readonly TreasureTracker tracker;
    private readonly OccultEventTracker occultEventTracker;
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly NavigationService navigationService;
    private readonly Action openCeWatch;
    private readonly Action openFateWatch;
    private readonly Action openConfiguration;
    private readonly Action save;
    private readonly Action toggleDetails;
    private uint loadedMapId;
    private GameMap? loadedMap;
    private ISharedImmediateTexture? mapTexture;
    private readonly Dictionary<uint, ISharedImmediateTexture> markerTextures = [];
    private readonly Dictionary<int, float> markerHoverScales = [];
    private Vector2 previousViewportSize;
    private Vector2 mapPan;
    private Vector2 mapMouseDown;
    private float mapZoom = 1f;
    private bool mapMouseHeld;
    private bool mapWasDragged;
    private int detailPage;
    private float lastAutoSizedWidth = -1f;
    private float lastAutoSizedHeaderHeight = -1f;

    public TreasureMapWindow(PluginConfiguration configuration, TreasureTracker tracker,
        OccultEventTracker occultEventTracker,
        IPartyList partyList, IPlayerState playerState, IClientState clientState,
        IDataManager dataManager, ITextureProvider textureProvider,
        NavigationService navigationService, Action openCeWatch, Action openFateWatch, Action openConfiguration, Action save,
        Action toggleDetails)
        : base("新月罗盘###CrescentCompass-TreasureMap")
    {
        this.configuration = configuration;
        this.tracker = tracker;
        this.occultEventTracker = occultEventTracker;
        this.partyList = partyList;
        this.playerState = playerState;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.navigationService = navigationService;
        this.openCeWatch = openCeWatch;
        this.openFateWatch = openFateWatch;
        this.openConfiguration = openConfiguration;
        this.save = save;
        this.toggleDetails = toggleDetails;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(320f, 340f),
            MaximumSize = new(float.MaxValue, float.MaxValue)
        };
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Priority = 2,
            Click = _ => this.openConfiguration(),
            ShowTooltip = () => ImGui.TextUnformatted("设置")
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Columns,
            Priority = 1,
            Click = _ => this.toggleDetails(),
            ShowTooltip = () => ImGui.TextUnformatted("显示或隐藏详情卡片")
        });
    }

    public override void Draw()
    {
        if (!tracker.IsSupportedTerritory)
        {
            IsOpen = false;
            return;
        }
        var colors = UiTheme.PushContentStyle(configuration.ComfortableUiDensity);
        try
        {
        ScreenPosition = ImGui.GetWindowPos();
        ScreenSize = ImGui.GetWindowSize();
        var headerStart = ImGui.GetCursorPosY();
        DrawHeader();
        var headerHeight = ImGui.GetCursorPosY() - headerStart;
        var available = ImGui.GetContentRegionAvail();
        var currentWindowSize = ImGui.GetWindowSize();
        var side = MathF.Max(220f, currentWindowSize.X - ImGui.GetStyle().WindowPadding.X * 2f);
        if (MathF.Abs(currentWindowSize.X - lastAutoSizedWidth) > 0.5f ||
            MathF.Abs(headerHeight - lastAutoSizedHeaderHeight) > 0.5f)
        {
            var desiredHeight = currentWindowSize.Y + side - available.Y;
            ImGui.SetWindowSize(new Vector2(currentWindowSize.X, desiredHeight), ImGuiCond.Always);
            lastAutoSizedWidth = currentWindowSize.X;
            lastAutoSizedHeaderHeight = headerHeight;
        }
        var size = new Vector2(side, side);
        ImGui.BeginGroup();
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("###CrescentCompass-MapCanvas", size);
        var mapClick = UpdateMapInteraction(origin, size);
        var renderedSize = size * mapZoom;
        var renderedOrigin = origin + (size - renderedSize) / 2f + mapPan;
        var draw = ImGui.GetWindowDrawList();
        var panelAlpha = (byte)(configuration.WindowOpacity * 255f);
        var mapAlpha = (byte)(configuration.MapTextureOpacity * 255f);
        var alpha = (byte)(configuration.MapMarkerOpacity * 255f);
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, Pack(14, 23, 31, panelAlpha), 8f);
        if (!DrawNativeMap(draw, renderedOrigin, renderedSize, mapAlpha))
        {
            draw.AddLine(origin + new Vector2(side / 2f, 0), origin + new Vector2(side / 2f, side), Pack(50, 65, 75, alpha));
            draw.AddLine(origin + new Vector2(0, side / 2f), origin + new Vector2(side, side / 2f), Pack(50, 65, 75, alpha));
        }

        foreach (var hint in tracker.VisibleHints)
            DrawHintSector(draw, hint, renderedOrigin, renderedSize, alpha);

        DrawDefaultMapMarkers(draw, renderedOrigin, renderedSize, alpha);

        if (configuration.ShowFieldTreasureMapMarkers)
            DrawFieldTreasurePoints(draw, renderedOrigin, renderedSize, alpha);

        if (configuration.ShowConfirmedFieldTreasureMapMarkers)
            DrawConfirmedFieldTreasures(draw, renderedOrigin, renderedSize, alpha);

        if (configuration.ShowMapRouteLayer || navigationService.IsRecordingCustomRoute)
            DrawNavigationPath(draw, renderedOrigin, renderedSize, alpha);

        DrawRouteRecordingDestination(draw, renderedOrigin, renderedSize, alpha);

        DrawAetheryteHitTargets(draw, renderedOrigin, renderedSize);

        foreach (var activeEvent in occultEventTracker.ActiveEvents)
            DrawActiveEvent(draw, activeEvent, renderedOrigin, renderedSize, alpha);

        foreach (var (position, index) in PartyMembers())
            DrawPartyMember(draw, WorldToCanvas(position, renderedOrigin, renderedSize), index, alpha);

        var mapHovered = ImGui.IsItemHovered();
        var pointer = ImGui.GetMousePos();
        var hoveredCandidateId = mapHovered
            ? tracker.VisibleCandidates
                .Select(candidate => (candidate.Id, Distance: Vector2.DistanceSquared(
                    WorldToCanvas(candidate.Position, renderedOrigin, renderedSize), pointer),
                    Radius: (tracker.FocusedCandidate?.Id == candidate.Id
                        ? 9f * configuration.FocusedCandidateIconScale
                        : 6.5f * configuration.CandidateIconScale) * ImGuiHelpers.GlobalScale))
                .Where(item => item.Distance <= item.Radius * item.Radius)
                .OrderBy(item => item.Distance)
                .Select(item => item.Id)
                .FirstOrDefault()
            : 0u;
        foreach (var candidate in tracker.VisibleCandidates)
        {
            var point = WorldToCanvas(candidate.Position, renderedOrigin, renderedSize);
            var focused = tracker.FocusedCandidate?.Id == candidate.Id;
            var radius = (focused
                    ? 9f * configuration.FocusedCandidateIconScale
                    : 6.5f * configuration.CandidateIconScale) * ImGuiHelpers.GlobalScale;
            radius *= UpdateMarkerHoverScale(HashCode.Combine(candidate.Id, 0x43414E44), candidate.Id == hoveredCandidateId);
            var color = focused ? Pack(255, 183, 40, alpha) : Pack(90, 214, 230, alpha);
            draw.AddCircleFilled(point, radius + 1.5f, Pack(0, 0, 0, (byte)Math.Min(alpha, (byte)180)), 20);
            draw.AddCircleFilled(point, radius, color, 20);
            draw.AddCircle(point, radius, Pack(255, 255, 255, alpha), 20, 1.25f);
            if (candidate.Id == hoveredCandidateId)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"候选 #{tracker.GetCandidateNumber(candidate):D2}");
                ImGui.TextDisabled(focused ? "当前关注 · 点击保持关注" : "点击关注此候选");
                ImGui.EndTooltip();
            }
        }

        if (tracker.ConfirmedTreasure is { } treasure)
            draw.AddCircleFilled(WorldToCanvas(treasure.Position, renderedOrigin, renderedSize), 7f, Pack(88, 216, 117, alpha));

        if (tracker.PlayerPosition is { } player && tracker.PlayerRotation is { } rotation)
            DrawPlayer(draw, WorldToCanvas(player, renderedOrigin, renderedSize), rotation, tracker.CameraRotation, alpha);

        if (mapClick is { } mouse)
            HandleMapClick(mouse, renderedOrigin, renderedSize);
        draw.PopClipRect();
        draw.AddRect(origin, origin + size, Pack(169, 179, 195, 150), 8f);
        ImGui.EndGroup();
        }
        finally
        {
            UiTheme.PopContentStyle(colors);
        }
    }

    public override void PreDraw()
    {
        BgAlpha = configuration.WindowOpacity;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Panel);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    public Vector2 ScreenPosition { get; private set; }

    public Vector2 ScreenSize { get; private set; }

    private void DrawHeader()
    {
        var forecastLines = occultEventTracker.ForecastStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (forecastLines.Length > 0) ImGui.TextDisabled(forecastLines[0]);
        if (forecastLines.Length > 1) ImGui.TextUnformatted(forecastLines[1]);
        if (tracker.ConfirmedTreasure != null)
            ImGui.TextColored(UiTheme.Green, $"已确认 · {tracker.ActualTreasureName}");
    }

    private void DrawMapToolbar()
    {
        if (ImGui.SmallButton("图层"))
        {
            configuration.ShowMapRouteLayer = !configuration.ShowMapRouteLayer;
            save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(configuration.ShowMapRouteLayer ? "隐藏导航路线" : "显示导航路线");
            ImGui.EndTooltip();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("−")) mapZoom = Math.Clamp(mapZoom / 1.15f, 0.2f, 5f);
        ImGui.SameLine();
        if (ImGui.SmallButton("复位"))
        {
            mapZoom = 1f;
            mapPan = Vector2.Zero;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("＋")) mapZoom = Math.Clamp(mapZoom * 1.15f, 0.2f, 5f);
        ImGui.SameLine();
        ImGui.TextDisabled($"{mapZoom * 100f:F0}%");
    }

    private void DrawDetailsPanel(Vector2 size)
    {
        ImGui.BeginChild("###CrescentCompass-MapDetails", size, true);
        var tabWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2f) / 3f;
        if (DrawDetailTab("寻宝", detailPage == 0, tabWidth)) detailPage = 0;
        ImGui.SameLine();
        if (DrawDetailTab("事件", detailPage == 1, tabWidth)) detailPage = 1;
        ImGui.SameLine();
        if (DrawDetailTab("路线", detailPage == 2, tabWidth)) detailPage = 2;
        ImGui.Separator();
        switch (detailPage)
        {
            case 0: DrawTreasureDetails(); break;
            case 1: DrawEventDetails(); break;
            default: DrawRouteDetails(); break;
        }
        ImGui.EndChild();
    }

    private static bool DrawDetailTab(string label, bool selected, float width)
    {
        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, UiTheme.Gold with { W = 0.56f });
        var clicked = ImGui.Button(label, new Vector2(width, 0f));
        if (selected) ImGui.PopStyleColor();
        return clicked;
    }

    private void DrawTreasureDetails()
    {
        ImGui.TextDisabled("剩余候选");
        ImGui.SetWindowFontScale(1.45f);
        ImGui.TextColored(UiTheme.Cyan, tracker.Session.Candidates.Count.ToString("D2"));
        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped(tracker.Status);
        if (tracker.FocusedCandidate is { } candidate)
        {
            UiTheme.SectionTitle($"当前关注 #{tracker.GetCandidateNumber(candidate):D2}");
            if (tracker.PlayerPosition is { } player)
            {
                var distance = MathF.Sqrt(MathF.Pow(player.X - candidate.Position.X, 2f) + MathF.Pow(player.Z - candidate.Position.Z, 2f));
                ImGui.TextUnformatted($"{distance:F0}m · X {candidate.Position.X:F1} · Z {candidate.Position.Z:F1}");
            }
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

    private void DrawEventDetails()
    {
        var events = occultEventTracker.ActiveEvents;
        ImGui.TextDisabled($"当前可见事件 · {events.Count}");
        foreach (var item in events.Take(8))
        {
            ImGui.PushID((int)item.DataId);
            ImGui.TextColored(item.Kind == OccultEventKind.CriticalEngagement ? UiTheme.Gold : UiTheme.Cyan,
                EventKindName(item.Kind));
            ImGui.SameLine();
            var nameWidth = ImGui.CalcTextSize(item.Name).X + ImGui.GetStyle().FramePadding.X * 2f;
            if (ImGui.Selectable($"{item.Name}##inline-event", false, ImGuiSelectableFlags.None, new Vector2(nameWidth, 0f)))
                navigationService.NavigateToEvent(item.Position, $"{EventKindName(item.Kind)}：{item.Name}",
                    item.DataId, CustomRouteKind(item.Kind));
            if (!string.IsNullOrEmpty(item.RewardTag))
            {
                ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(RewardTagColor(item.RewardTag), item.RewardTag);
            }
            if (item.Kind == OccultEventKind.CriticalEngagement &&
                OccultEventRewardCatalog.TryGetSoulShard(clientState.TerritoryType, item.DataId, out var soulShard))
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
        if (ImGui.Button("打开 Ｃ Ｅ 速查")) openCeWatch();
        if (ImGui.Button("打开 FATE 速查")) openFateWatch();
    }

    private void DrawRouteDetails()
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
        UiTheme.SectionTitle("路线图层");
        var showRoute = configuration.ShowMapRouteLayer;
        if (ImGui.Checkbox("在地图上显示实际路径", ref showRoute))
        {
            configuration.ShowMapRouteLayer = showRoute;
            save();
        }
        ImGui.TextDisabled(navigationService.DisplayPath.Count > 1
            ? $"当前路径 {navigationService.DisplayPath.Count} 个节点"
            : "只有 vnavmesh 返回有效路径后才绘制路线。");
    }

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

    private void DrawNavigationPath(ImDrawListPtr draw, Vector2 origin, Vector2 size, byte alpha)
    {
        var path = navigationService.DisplayPath;
        if (path.Count < 2) return;
        var color = Pack(90, 214, 230, (byte)Math.Min(alpha, (byte)220));
        for (var index = 1; index < path.Count; index++)
            draw.AddLine(WorldToCanvas(path[index - 1], origin, size), WorldToCanvas(path[index], origin, size), color, 2.5f);
    }

    private void DrawRouteRecordingDestination(ImDrawListPtr draw, Vector2 origin, Vector2 size, byte alpha)
    {
        if (!navigationService.IsRecordingCustomRoute ||
            navigationService.RecordingDestination is not { } destination)
            return;
        var center = WorldToCanvas(destination, origin, size);
        var color = Pack(255, 205, 70, alpha);
        var scale = ImGuiHelpers.GlobalScale;
        var pulse = configuration.ReduceMotion ? 0f : (float)(ImGui.GetTime() % 1.2) / 1.2f * 10f;
        draw.AddCircle(center, 10f * scale + pulse, color, 28, 2.5f * scale);
        draw.AddCircleFilled(center, 4f * scale, color, 16);
        draw.AddLine(center, center + new Vector2(0f, -22f * scale), color, 2f * scale);
        draw.AddText(center + new Vector2(7f, -34f) * scale, color, "路线终点");
    }

    private IEnumerable<(Vector3 Position, int Index)> PartyMembers()
    {
        var index = 0;
        foreach (var member in partyList)
        {
            index++;
            if (member == null || member.ContentId == playerState.ContentId ||
                member.Territory.RowId != clientState.TerritoryType || !PotPredictionSession.IsFinite(member.Position))
                continue;
            yield return (member.Position, index);
        }
    }

    private static void DrawPlayer(
        ImDrawListPtr draw,
        Vector2 center,
        float rotation,
        float? cameraRotation,
        byte alpha)
    {
        if (cameraRotation is { } camera)
        {
            var cameraDirection = Direction(camera);
            var cameraLeft = Rotate(cameraDirection, -0.48f);
            var cameraRight = Rotate(cameraDirection, 0.48f);
            const float cameraLength = 30f;
            draw.AddTriangleFilled(center, center + cameraLeft * cameraLength, center + cameraRight * cameraLength,
                Pack(255, 200, 63, (byte)Math.Min((int)alpha, 50)));
            draw.AddLine(center, center + cameraLeft * cameraLength,
                Pack(255, 215, 96, (byte)Math.Min((int)alpha, 192)), 1.5f);
            draw.AddLine(center, center + cameraRight * cameraLength,
                Pack(255, 215, 96, (byte)Math.Min((int)alpha, 192)), 1.5f);
        }

        var facing = Direction(rotation);
        var normal = new Vector2(-facing.Y, facing.X);
        var tip = center + facing * 13f;
        var tail = center - facing * 7f;
        draw.AddTriangleFilled(tip, tail + normal * 7f, tail - normal * 7f, Pack(0, 0, 0, alpha));
        draw.AddTriangleFilled(tip - facing * 2f, tail + facing + normal * 5f, tail + facing - normal * 5f,
            Pack(66, 179, 255, alpha));
    }

    private void DrawPartyMember(ImDrawListPtr draw, Vector2 center, int index, byte alpha)
    {
        var scale = ImGuiHelpers.GlobalScale * configuration.PartyMemberIconScale;
        var radius = 9f * scale;
        draw.AddCircleFilled(center, radius + 2f, Pack(0, 0, 0, (byte)Math.Min((int)alpha, 208)), 20);
        draw.AddCircleFilled(center, radius, Pack(62, 169, 236, alpha), 20);
        draw.AddCircle(center, radius, Pack(255, 255, 255, alpha), 20, 1.5f);
        var number = index.ToString();
        var textSize = ImGui.CalcTextSize(number) * scale;
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, center - textSize / 2f,
            Pack(255, 255, 255, alpha), number);
    }

    private void DrawHintSector(ImDrawListPtr draw, PotHint hint, Vector2 origin, Vector2 size, byte alpha)
    {
        const int segments = 24;
        const float mapDistanceLimit = 3000f;
        var direction = hint.DirectionVector;
        var centerAngle = MathF.Atan2(direction.X, -direction.Y);
        var halfAngle = (PotHint.SectorHalfAngleDegrees + PotHint.AngleToleranceDegrees) * MathF.PI / 180f;
        var inner = Math.Max(0f, hint.MinimumDistance - PotHint.DistanceTolerance);
        var outer = float.IsPositiveInfinity(hint.MaximumDistance)
            ? mapDistanceLimit
            : hint.MaximumDistance + PotHint.DistanceTolerance;
        var fill = Pack(240, 200, 48, (byte)Math.Min((int)alpha, 32));
        var outline = Pack(250, 216, 80, (byte)Math.Min((int)alpha, 192));

        Vector2 Point(float angle, float distance) => WorldToCanvas(new(
            hint.Origin.X + MathF.Sin(angle) * distance,
            hint.Origin.Y,
            hint.Origin.Z - MathF.Cos(angle) * distance), origin, size);

        var previousInner = Point(centerAngle - halfAngle, inner);
        var previousOuter = Point(centerAngle - halfAngle, outer);
        for (var index = 1; index <= segments; index++)
        {
            var angle = centerAngle - halfAngle + 2f * halfAngle * index / segments;
            var currentInner = Point(angle, inner);
            var currentOuter = Point(angle, outer);
            draw.AddQuadFilled(previousInner, previousOuter, currentOuter, currentInner, fill);
            draw.AddLine(previousOuter, currentOuter, outline, 1.5f);
            previousInner = currentInner;
            previousOuter = currentOuter;
        }
        draw.AddLine(Point(centerAngle - halfAngle, inner), Point(centerAngle - halfAngle, outer), outline, 1.5f);
        draw.AddLine(Point(centerAngle + halfAngle, inner), Point(centerAngle + halfAngle, outer), outline, 1.5f);
    }

    private static Vector2 Direction(float rotation) => new(MathF.Sin(rotation), MathF.Cos(rotation));

    private static Vector2 Rotate(Vector2 vector, float angle)
    {
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        return new(
            vector.X * cosine - vector.Y * sine,
            vector.X * sine + vector.Y * cosine);
    }

    private Vector2 WorldToCanvas(Vector3 position, Vector2 origin, Vector2 size)
    {
        if (loadedMap is { } map)
        {
            var scale = map.SizeFactor / 100f;
            var texture = (new Vector2(position.X, position.Z) + new Vector2(map.OffsetX, map.OffsetY)) * scale
                          + new Vector2(1024f);
            return origin + texture / 2048f * size;
        }

        var x = Math.Clamp((position.X - WorldMinimum) / (WorldMaximum - WorldMinimum), 0f, 1f);
        var z = Math.Clamp((position.Z - WorldMinimum) / (WorldMaximum - WorldMinimum), 0f, 1f);
        return origin + new Vector2(x * size.X, z * size.Y);
    }

    private bool DrawNativeMap(ImDrawListPtr draw, Vector2 origin, Vector2 size, byte alpha)
    {
        EnsureMapLoaded();
        if (mapTexture == null) return false;
        var texture = mapTexture.GetWrapOrEmpty();
        if (texture.Handle == nint.Zero || texture.Width < 64 || texture.Height < 64) return false;
        draw.AddImage(texture.Handle, origin, origin + size, Vector2.Zero, Vector2.One,
            Pack(255, 255, 255, alpha));
        return true;
    }

    private void DrawDefaultMapMarkers(ImDrawListPtr draw, Vector2 origin, Vector2 size, byte alpha)
    {
        if (loadedMap is not { } map || map.MapMarkerRange == 0) return;
        var row = dataManager.GetSubrowExcelSheet<GameMapMarker>().GetRowOrDefault(map.MapMarkerRange);
        if (row is not { } markers) return;
        foreach (var marker in markers)
        {
            if (marker.Icon == 0) continue;
            var iconId = (uint)marker.Icon;
            if (!markerTextures.TryGetValue(iconId, out var sharedTexture))
            {
                sharedTexture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId, false, true, null));
                markerTextures[iconId] = sharedTexture;
            }
            var texture = sharedTexture.GetWrapOrEmpty();
            if (texture.Handle == nint.Zero) continue;
            var center = origin + new Vector2(marker.X, marker.Y) / 2048f * size;
            var aetheryte = IsAetheryteMarker(center, origin, size);
            var baseSize = (aetheryte ? 24f * configuration.AetheryteIconScale : 32f * configuration.LandmarkIconScale) *
                           ImGuiHelpers.GlobalScale;
            var hoverScale = UpdateMarkerHoverScale(HashCode.Combine(iconId, marker.X, marker.Y), center, baseSize / 2f);
            var halfSize = new Vector2(baseSize * hoverScale / 2f);
            draw.AddImage(texture.Handle, center - halfSize, center + halfSize,
                Vector2.Zero, Vector2.One, Pack(255, 255, 255, alpha));
        }
    }

    private void DrawFieldTreasurePoints(ImDrawListPtr draw, Vector2 origin, Vector2 size, byte alpha)
    {
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        FieldTreasurePoint? hoveredPoint = null;
        foreach (var point in tracker.FieldTreasurePoints)
        {
            var center = WorldToCanvas(point.Position, origin, size);
            var color = point.Kind switch
            {
                FieldTreasureKind.Silver => Pack(215, 230, 245, alpha),
                FieldTreasureKind.Bronze => Pack(224, 145, 70, alpha),
                _ => Pack(55, 190, 245, alpha)
            };
            var radius = 5f * ImGuiHelpers.GlobalScale * configuration.FieldTreasurePointIconScale;
            draw.AddCircleFilled(center, radius + 2f, Pack(0, 0, 0, (byte)Math.Min(alpha, (byte)210)), 16);
            draw.AddCircleFilled(center, radius, color, 16);
            draw.AddCircle(center, radius, Pack(255, 255, 255, alpha), 16, 1.25f);
            if (hovered && Vector2.DistanceSquared(mouse, center) <= radius * radius * 4f)
                hoveredPoint = point;
        }

        if (hoveredPoint is not { } item) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(item.Kind switch
        {
            FieldTreasureKind.Silver => "银宝箱静态点位",
            FieldTreasureKind.Bronze => "铜宝箱静态点位",
            _ => "铜／银宝箱静态点位"
        });
        ImGui.TextDisabled("表示可能刷新位置");
        ImGui.EndTooltip();
    }

    private void DrawConfirmedFieldTreasures(ImDrawListPtr draw, Vector2 origin, Vector2 size, byte alpha)
    {
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        FieldTreasureSnapshot? hoveredChest = null;
        foreach (var chest in tracker.FieldTreasures)
        {
            var center = WorldToCanvas(chest.Position, origin, size);
            var color = chest.Kind switch
            {
                FieldTreasureKind.Silver => Pack(225, 240, 255, alpha),
                FieldTreasureKind.Bronze => Pack(240, 145, 55, alpha),
                _ => Pack(255, 205, 70, alpha)
            };
            var radius = 8f * ImGuiHelpers.GlobalScale * configuration.ConfirmedFieldTreasureIconScale;
            draw.AddCircleFilled(center, radius + 3f, Pack(0, 0, 0, 225), 20);
            draw.AddCircleFilled(center, radius, color, 20);
            draw.AddCircle(center, radius, Pack(255, 255, 255, alpha), 20, 2f);
            draw.AddCircle(center, radius + 4f, color, 20, 1.5f);
            if (hovered && Vector2.DistanceSquared(mouse, center) <= (radius + 5f) * (radius + 5f))
                hoveredChest = chest;
        }

        if (hoveredChest is not { } item) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(item.Kind switch
        {
            FieldTreasureKind.Silver => "银宝箱",
            FieldTreasureKind.Bronze => "铜宝箱",
            _ => "普通宝箱"
        });
        ImGui.TextColored(UiTheme.Green, "当前确认存在");
        if (tracker.PlayerPosition is { } player)
        {
            var dx = player.X - item.Position.X;
            var dz = player.Z - item.Position.Z;
            ImGui.TextDisabled($"距离 {MathF.Sqrt(dx * dx + dz * dz):F0}m");
        }
        ImGui.EndTooltip();
    }

    private void DrawAetheryteHitTargets(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        foreach (var aetheryte in CrescentAetheryteCatalog.ForTerritory(clientState.TerritoryType))
        {
            var center = WorldToCanvas(aetheryte.Position, origin, size);
            if (navigationService.ActiveDestination is { } destination &&
                Vector3.DistanceSquared(destination, aetheryte.Position) < 1f)
                DrawNavigationPulse(draw, center);
            var hoverRadius = 12f * ImGuiHelpers.GlobalScale * configuration.AetheryteIconScale *
                              (configuration.ReduceMotion ? 1f : configuration.MapIconHoverScale);
            if (!hovered || Vector2.DistanceSquared(mouse, center) > hoverRadius * hoverRadius) continue;
            var name = CrescentAetheryteCatalog.Name(aetheryte, dataManager);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(name);
            ImGui.TextDisabled(navigationService.IsNavigating
                ? "点击：切换目标；再次点击当前目标可取消"
                : "点击：前往附近水晶并传送");
            ImGui.EndTooltip();
            break;
        }
    }

    private void HandleMapClick(Vector2 mouse, Vector2 origin, Vector2 size)
    {
        var activeEvent = occultEventTracker.ActiveEvents
            .Select(item => (Event: item, Distance: Vector2.DistanceSquared(
                WorldToCanvas(item.Position, origin, size), mouse)))
            .Where(item => item.Distance <= EventHitRadius(item.Event.Kind) * EventHitRadius(item.Event.Kind))
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (activeEvent.Event.DataId != 0)
        {
            navigationService.NavigateToEvent(activeEvent.Event.Position,
                $"{EventKindName(activeEvent.Event.Kind)}：{activeEvent.Event.Name}",
                activeEvent.Event.DataId, CustomRouteKind(activeEvent.Event.Kind));
            return;
        }

        var aetheryte = CrescentAetheryteCatalog.ForTerritory(clientState.TerritoryType)
            .Select(item => (Aetheryte: item, Distance: Vector2.DistanceSquared(
                WorldToCanvas(item.Position, origin, size), mouse)))
            .Where(item => item.Distance <= MathF.Pow(12f * ImGuiHelpers.GlobalScale *
                configuration.AetheryteIconScale * configuration.MapIconHoverScale, 2f))
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (aetheryte.Aetheryte.DataId != 0)
        {
            navigationService.TravelToAetheryte(aetheryte.Aetheryte,
                CrescentAetheryteCatalog.Name(aetheryte.Aetheryte, dataManager));
            return;
        }

        var closest = tracker.VisibleCandidates
            .Select(candidate => (Candidate: candidate, Distance: Vector2.DistanceSquared(
                WorldToCanvas(candidate.Position, origin, size), mouse)))
            .Where(item => item.Distance <= MathF.Pow(9f * ImGuiHelpers.GlobalScale *
                MathF.Max(configuration.CandidateIconScale, configuration.FocusedCandidateIconScale), 2f))
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (closest.Candidate.Id != 0) tracker.Focus(closest.Candidate.Id);
    }

    private static CustomNavigationRouteKind? CustomRouteKind(OccultEventKind kind) => kind switch
    {
        OccultEventKind.CriticalEngagement => CustomNavigationRouteKind.CriticalEngagement,
        OccultEventKind.Fate or OccultEventKind.MagicPot or OccultEventKind.MagicPotForecast =>
            CustomNavigationRouteKind.Fate,
        _ => null
    };

    private void DrawActiveEvent(
        ImDrawListPtr draw,
        OccultEventSnapshot activeEvent,
        Vector2 origin,
        Vector2 size,
        byte alpha)
    {
        var center = WorldToCanvas(activeEvent.Position, origin, size);
        var markerSize = activeEvent.Kind switch
        {
            OccultEventKind.CriticalEngagement => 30f,
            OccultEventKind.ForkTower => 32f,
            OccultEventKind.MagicPotForecast => 32f,
            OccultEventKind.MagicPot => 28f,
            _ => 26f
        };
        markerSize *= ImGuiHelpers.GlobalScale * EventScale(activeEvent.Kind);
        var hoverScale = UpdateMarkerHoverScale(HashCode.Combine(activeEvent.DataId, (int)activeEvent.Kind),
            center, markerSize / 2f);
        var halfSize = new Vector2(markerSize * hoverScale / 2f);
        if (navigationService.ActiveDestination is { } navigationDestination &&
            Vector3.DistanceSquared(navigationDestination, activeEvent.Position) < 1f)
            DrawNavigationPulse(draw, center);
        if (activeEvent.Kind == OccultEventKind.MagicPotForecast && !configuration.ReduceMotion)
        {
            var phase = (float)(ImGui.GetTime() % 1.2) / 1.2f;
            var pulseRadius = markerSize * 0.5f + phase * 25f;
            var pulseAlpha = (byte)Math.Min((int)alpha, (int)((1f - phase) * 255f));
            draw.AddCircle(center, pulseRadius, Pack(255, 200, 53, pulseAlpha), 32, 2f);
        }
        var drewIcon = false;
        if (activeEvent.IconId != 0)
        {
            if (!markerTextures.TryGetValue(activeEvent.IconId, out var sharedTexture))
            {
                sharedTexture = textureProvider.GetFromGameIcon(
                    new GameIconLookup(activeEvent.IconId, false, true, null));
                markerTextures[activeEvent.IconId] = sharedTexture;
            }
            var texture = sharedTexture.GetWrapOrEmpty();
            if (texture.Handle != nint.Zero)
            {
                draw.AddImage(texture.Handle, center - halfSize, center + halfSize,
                    Vector2.Zero, Vector2.One, Pack(255, 255, 255, alpha));
                drewIcon = true;
            }
        }
        if (!drewIcon)
        {
            draw.AddCircleFilled(center, markerSize / 2f, Pack(224, 116, 54, alpha), 20);
            draw.AddCircle(center, markerSize / 2f, Pack(255, 255, 255, alpha), 20, 1.5f);
        }

        if (!ImGui.IsItemHovered() || Vector2.DistanceSquared(ImGui.GetMousePos(), center) > markerSize * markerSize / 4f)
            return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"{EventKindName(activeEvent.Kind)}：{activeEvent.Name}");
        if (!string.IsNullOrEmpty(activeEvent.RewardTag))
        {
            ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(RewardTagColor(activeEvent.RewardTag), activeEvent.RewardTag);
        }
        if (activeEvent.Kind == OccultEventKind.CriticalEngagement &&
            OccultEventRewardCatalog.TryGetSoulShard(clientState.TerritoryType, activeEvent.DataId, out var soulShard))
        {
            ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(SoulShardTagColor(soulShard.Tag), soulShard.Tag);
        }
        ImGui.TextDisabled(activeEvent.StateText);
        ImGui.EndTooltip();
    }

    private static string EventKindName(OccultEventKind kind) => kind switch
    {
        OccultEventKind.CriticalEngagement => "CE",
        OccultEventKind.ForkTower => "两歧塔",
        OccultEventKind.MagicPot => "魔法罐",
        OccultEventKind.MagicPotForecast => "魔法罐预告",
        _ => "FATE"
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

    private void DrawNavigationPulse(ImDrawListPtr draw, Vector2 center)
    {
        if (configuration.ReduceMotion)
        {
            draw.AddCircle(center, 18f * ImGuiHelpers.GlobalScale, Pack(255, 183, 40, 230), 32, 2f);
            return;
        }
        var phase = (float)(ImGui.GetTime() % 1.2) / 1.2f;
        var radius = 12f + phase * 25f;
        var alpha = (byte)((1f - phase) * 255f);
        draw.AddCircle(center, radius, Pack(255, 200, 53, alpha), 32, 2f);
    }

    private float UpdateMarkerHoverScale(int key, Vector2 center, float radius)
    {
        var hovered = ImGui.IsItemHovered() &&
                      Vector2.DistanceSquared(ImGui.GetMousePos(), center) <= radius * radius;
        return UpdateMarkerHoverScale(key, hovered);
    }

    private float UpdateMarkerHoverScale(int key, bool hovered)
    {
        var target = hovered && !configuration.ReduceMotion ? configuration.MapIconHoverScale : 1f;
        markerHoverScales.TryGetValue(key, out var current);
        if (current <= 0f) current = 1f;
        var speed = 1f - MathF.Exp(-12f * ImGui.GetIO().DeltaTime);
        current += (target - current) * speed;
        if (!hovered && MathF.Abs(current - 1f) < 0.002f)
            markerHoverScales.Remove(key);
        else
            markerHoverScales[key] = current;
        return current;
    }

    private Vector2? UpdateMapInteraction(Vector2 origin, Vector2 size)
    {
        if (previousViewportSize == Vector2.Zero)
        {
            previousViewportSize = size;
        }
        else if (Vector2.DistanceSquared(previousViewportSize, size) > 1f)
        {
            mapPan = new Vector2(
                mapPan.X * size.X / previousViewportSize.X,
                mapPan.Y * size.Y / previousViewportSize.Y);
            previousViewportSize = size;
        }

        var hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        if (hovered && io.MouseWheel != 0f)
        {
            var mouse = ImGui.GetMousePos();
            var oldSize = size * mapZoom;
            var oldOrigin = origin + (size - oldSize) / 2f + mapPan;
            var texturePoint = (mouse - oldOrigin) / oldSize;
            mapZoom = Math.Clamp(mapZoom * (1f + io.MouseWheel * 0.12f), 0.2f, 5f);
            var newSize = size * mapZoom;
            mapPan = mouse - texturePoint * newSize - origin - (size - newSize) / 2f;
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            mapPan = Vector2.Zero;
            mapZoom = 1f;
            mapMouseHeld = false;
            return null;
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            mapMouseHeld = true;
            mapWasDragged = false;
            mapMouseDown = ImGui.GetMousePos();
        }

        if (mapMouseHeld && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (mapWasDragged || Vector2.Distance(ImGui.GetMousePos(), mapMouseDown) > 16f)
            {
                mapWasDragged = true;
                mapPan += io.MouseDelta;
            }
        }

        if (!mapMouseHeld || !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return null;
        mapMouseHeld = false;
        return mapWasDragged ? null : ImGui.GetMousePos();
    }

    private bool IsAetheryteMarker(Vector2 center, Vector2 origin, Vector2 size) =>
        CrescentAetheryteCatalog.ForTerritory(clientState.TerritoryType)
            .Any(item => Vector2.DistanceSquared(center, WorldToCanvas(item.Position, origin, size)) <=
                         MathF.Pow(10f * ImGuiHelpers.GlobalScale, 2f));

    private float EventScale(OccultEventKind kind) => configuration.EventIconScale * (kind switch
    {
        OccultEventKind.CriticalEngagement => configuration.CeIconScale,
        OccultEventKind.Fate => configuration.FateIconScale,
        OccultEventKind.MagicPot => configuration.MagicPotIconScale,
        OccultEventKind.MagicPotForecast => configuration.ForecastIconScale,
        _ => configuration.LandmarkIconScale
    });

    private float EventHitRadius(OccultEventKind kind)
    {
        var baseSize = kind switch
        {
            OccultEventKind.CriticalEngagement => 30f,
            OccultEventKind.ForkTower => 32f,
            OccultEventKind.MagicPotForecast => 32f,
            OccultEventKind.MagicPot => 28f,
            _ => 26f
        };
        return baseSize * ImGuiHelpers.GlobalScale * EventScale(kind) *
               (configuration.ReduceMotion ? 1f : configuration.MapIconHoverScale) / 2f;
    }

    private void EnsureMapLoaded()
    {
        var mapId = clientState.MapId;
        if (mapId == loadedMapId && loadedMap != null) return;
        loadedMapId = mapId;
        loadedMap = dataManager.GetExcelSheet<GameMap>().GetRowOrDefault(mapId);
        mapTexture = null;
        if (loadedMap is not { } map) return;
        var id = map.Id.ToString();
        if (string.IsNullOrWhiteSpace(id)) return;
        mapTexture = textureProvider.GetFromGame($"ui/map/{id}/{id.Replace("/", string.Empty)}_m.tex");
    }

    private static uint Pack(byte red, byte green, byte blue, byte alpha) =>
        (uint)(red | green << 8 | blue << 16 | alpha << 24);
}
