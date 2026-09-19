using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DynamicEventRow = Lumina.Excel.Sheets.DynamicEvent;
using FateRow = Lumina.Excel.Sheets.Fate;

namespace CrescentCompass.Windows;

public sealed class AutomationWatchWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly OccultEventTracker eventTracker;
    private readonly NavigationService navigationService;
    private readonly Action save;
    private readonly Dictionary<uint, string> ceNames = [];
    private readonly Dictionary<uint, string> fateNames = [];
    private string search = string.Empty;
    private int typeFilter;
    private int islandFilter;
    private bool onlySelected;

    public AutomationWatchWindow(PluginConfiguration configuration, IClientState clientState,
        IDataManager dataManager, OccultEventTracker eventTracker, NavigationService navigationService, Action save)
        : base("自动参与列表###CrescentCompass-AutomationWatch")
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.eventTracker = eventTracker;
        this.navigationService = navigationService;
        this.save = save;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(720f, 360f),
            MaximumSize = new(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        var colors = UiTheme.PushContentStyle(configuration.ComfortableUiDensity);
        try
        {
            if (navigationService.IsRecordingCustomRoute ||
                !navigationService.RouteRecordingStatus.StartsWith("尚未", StringComparison.Ordinal))
            {
                ImGui.TextColored(UiTheme.Cyan, navigationService.RouteRecordingStatus);
                if (navigationService.IsRecordingCustomRoute)
                    ImGui.TextDisabled("沿地图和场景中的终点标记行走，抵达后自动保存；也可在事件行手动保存。");
            }

            ImGui.SetNextItemWidth(210f);
            ImGui.InputTextWithHint("###CrescentCompass-AutomationSearch", "搜索事件名称或 ID", ref search, 100);
            ImGui.SameLine();
            ImGui.Checkbox("仅已选择", ref onlySelected);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(95f);
            ImGui.Combo("###CrescentCompass-AutomationTypeFilter", ref typeFilter, "全部\0CE\0FATE\0");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(95f);
            ImGui.Combo("###CrescentCompass-AutomationIslandFilter", ref islandFilter, "全部\0南部\0北部\0");

            var ceEntries = typeFilter == 2 ? [] : CeSpawnCatalog.All.Where(Matches).ToArray();
            var fateEntries = typeFilter == 1 ? [] : FateCatalog.All.Where(Matches).ToArray();
            ImGui.TextDisabled($"{ceEntries.Length + fateEntries.Length} 项 · 已选择 " +
                               $"{configuration.AutomatedCeIds.Count} 个 CE、" +
                               $"{configuration.AutomatedFateIds.Count} 个 FATE");

            if (ImGui.Button("选择筛选结果"))
            {
                foreach (var item in ceEntries) configuration.AutomatedCeIds.Add(item.Id);
                foreach (var item in fateEntries) configuration.AutomatedFateIds.Add(item.Id);
                save();
            }
            ImGui.SameLine();
            if (ImGui.Button("清除筛选结果"))
            {
                foreach (var item in ceEntries) configuration.AutomatedCeIds.Remove(item.Id);
                foreach (var item in fateEntries) configuration.AutomatedFateIds.Remove(item.Id);
                save();
            }
            ImGui.Separator();

            if (ceEntries.Length + fateEntries.Length == 0)
            {
                ImGui.TextDisabled("没有符合当前筛选条件的事件。");
                if (ImGui.Button("清除筛选"))
                {
                    search = string.Empty;
                    typeFilter = 0;
                    islandFilter = 0;
                    onlySelected = false;
                }
                return;
            }

            if (!ImGui.BeginTable("###CrescentCompass-AutomationTable", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp))
                return;
            ImGui.TableSetupColumn("参与", ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableSetupColumn("区域", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("事件", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("路线", ImGuiTableColumnFlags.WidthFixed, 176f);
            ImGui.TableHeadersRow();

            foreach (var entry in ceEntries)
                DrawRow(entry.Id, entry.TerritoryId, "CE", CeName(entry), configuration.AutomatedCeIds);
            foreach (var entry in fateEntries)
                DrawRow(entry.Id, entry.TerritoryId, "FATE", FateName(entry), configuration.AutomatedFateIds);
            ImGui.EndTable();
        }
        finally
        {
            UiTheme.PopContentStyle(colors);
        }
    }

    public override void PreDraw() => ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Panel);
    public override void PostDraw() => ImGui.PopStyleColor();

    public override void OnOpen()
    {
        islandFilter = clientState.TerritoryType switch
        {
            PotCandidateCatalog.SouthHornTerritoryId => 1,
            PotCandidateCatalog.NorthHornTerritoryId => 2,
            _ => islandFilter
        };
    }

    private bool Matches(CeSpawnDefinition entry)
    {
        if (!MatchesTerritory(entry.TerritoryId)) return false;
        if (onlySelected && !configuration.AutomatedCeIds.Contains(entry.Id)) return false;
        var term = search.Trim();
        var name = CeName(entry);
        return term.Length == 0 || entry.Id.ToString().Contains(term) ||
               name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private bool Matches(FateDefinition entry)
    {
        if (!MatchesTerritory(entry.TerritoryId)) return false;
        if (onlySelected && !configuration.AutomatedFateIds.Contains(entry.Id)) return false;
        var term = search.Trim();
        var name = FateName(entry);
        return term.Length == 0 || entry.Id.ToString().Contains(term) ||
               name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesTerritory(uint territoryId) => islandFilter switch
    {
        1 => territoryId == PotCandidateCatalog.SouthHornTerritoryId,
        2 => territoryId == PotCandidateCatalog.NorthHornTerritoryId,
        _ => true
    };

    private void DrawRow(uint id, uint territoryId, string kind, string name, HashSet<uint> selectedIds)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var selected = selectedIds.Contains(id);
        if (ImGui.Checkbox($"###auto-{kind}-{id}", ref selected))
        {
            if (selected) selectedIds.Add(id);
            else selectedIds.Remove(id);
            save();
        }
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(kind);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(territoryId == PotCandidateCatalog.SouthHornTerritoryId ? "南部" : "北部");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{name}  #{id}");

        var rewardTag = kind == "CE"
            ? OccultEventRewardCatalog.CriticalEncounterTag(territoryId, id)
            : OccultEventRewardCatalog.FateTag(territoryId, id);
        if (!string.IsNullOrEmpty(rewardTag))
        {
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(RewardTagColor(rewardTag), rewardTag);
            DrawTagTooltip("半魂晶掉落颜色");
        }
        if (kind == "CE" && OccultEventRewardCatalog.TryGetSoulShard(territoryId, id, out var soulShard))
        {
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(SoulShardTagColor(soulShard.Tag), soulShard.Tag);
            DrawTagTooltip($"灵魂碎晶：{soulShard.JobName}");
        }

        ImGui.TableNextColumn();
        DrawRouteControls(id, territoryId, kind, name);
    }

    private void DrawRouteControls(uint id, uint territoryId, string kind, string name)
    {
        var routeKind = kind == "CE"
            ? CustomNavigationRouteKind.CriticalEngagement
            : CustomNavigationRouteKind.Fate;
        var effectiveRoutes = navigationService.EffectiveNavigationRoutes(territoryId, id, routeKind);
        var count = effectiveRoutes.Count;
        var recordingThis = navigationService.IsRecordingCustomRoute &&
                            navigationService.RecordingEventId == id &&
                            navigationService.RecordingRouteKind == routeKind;
        if (recordingThis)
        {
            if (ImGui.SmallButton($"保存##route-save-{kind}-{id}"))
                navigationService.FinishCustomRouteRecording();
            ImGui.SameLine(0f, 3f);
            if (ImGui.SmallButton($"取消##route-cancel-{kind}-{id}"))
                navigationService.CancelCustomRouteRecording();
            return;
        }

        var destination = ResolveRecordingDestination(id, territoryId, routeKind);
        var teleportDisabled = clientState.TerritoryType != territoryId || destination is null;
        if (teleportDisabled)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"传送##route-teleport-{kind}-{id}") && destination is { } eventPosition)
            navigationService.TravelToNearestAetheryte(eventPosition, name);
        if (teleportDisabled)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && destination is null)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("该 CE 尚未出现且没有已录路线，暂时无法确定最近水晶。");
            ImGui.EndTooltip();
        }
        ImGui.SameLine(0f, 3f);
        var recordingDisabled = navigationService.IsRecordingCustomRoute || clientState.TerritoryType != territoryId;
        if (recordingDisabled)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"录制##route-record-{kind}-{id}"))
            navigationService.BeginCustomRouteRecording(
                territoryId, id, routeKind, name, destination);
        if (recordingDisabled)
            ImGui.EndDisabled();
        if (count <= 0) return;
        ImGui.SameLine(0f, 4f);
        var sourceLabel = effectiveRoutes.Any(NavigationService.IsBuiltInRoute) ? "内置" : "用户";
        ImGui.TextDisabled($"{count}条·{sourceLabel}");
    }

    private Vector3? ResolveRecordingDestination(
        uint id, uint territoryId, CustomNavigationRouteKind routeKind)
    {
        var active = eventTracker.ActiveEvents.FirstOrDefault(item => item.DataId == id);
        if (active.DataId != 0 && PotPredictionSession.IsFinite(active.Position))
            return active.Position;

        if (routeKind == CustomNavigationRouteKind.Fate)
        {
            var fate = FateCatalog.All.FirstOrDefault(item => item.Id == id && item.TerritoryId == territoryId);
            if (fate != null && fate.MapPosition != default && TryMapToWorld(fate.MapPosition, out var destination))
                return destination;
        }

        var previous = navigationService.EffectiveNavigationRoutes(territoryId, id, routeKind)
            .LastOrDefault(route => route.Points.Count > 0);
        return previous == null
            ? null
            : new Vector3(previous.Points[^1].X, previous.Points[^1].Y, previous.Points[^1].Z);
    }

    private bool TryMapToWorld(Vector2 mapPosition, out Vector3 destination)
    {
        destination = default;
        var map = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>().GetRowOrDefault(clientState.MapId);
        if (map is not { } row || row.SizeFactor == 0) return false;
        var scale = row.SizeFactor / 100f;
        var texture = (mapPosition - Vector2.One) * scale / 40.96f * 2048f;
        var world = (texture - new Vector2(1024f)) / scale - new Vector2(row.OffsetX, row.OffsetY);
        destination = new Vector3(world.X, 0f, world.Y);
        return PotPredictionSession.IsFinite(destination);
    }

    private string CeName(CeSpawnDefinition entry)
    {
        if (ceNames.TryGetValue(entry.Id, out var name)) return name;
        var localized = dataManager.GetExcelSheet<DynamicEventRow>().GetRowOrDefault(entry.Id)?.Name.ToString();
        name = string.IsNullOrWhiteSpace(localized) ? entry.EnglishName : localized;
        ceNames[entry.Id] = name;
        return name;
    }

    private string FateName(FateDefinition entry)
    {
        if (fateNames.TryGetValue(entry.Id, out var name)) return name;
        var localized = dataManager.GetExcelSheet<FateRow>().GetRowOrDefault(entry.Id)?.Name.ToString();
        name = string.IsNullOrWhiteSpace(localized) ? entry.EnglishName : localized;
        fateNames[entry.Id] = name;
        return name;
    }

    private static void DrawTagTooltip(string text)
    {
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
    }

    private static Vector4 RewardTagColor(string tag) => tag switch
    {
        "[黄]" => new(0.96f, 0.83f, 0.37f, 1f),
        "[青]" => new(0.35f, 0.84f, 0.90f, 1f),
        "[碧]" => new(0.34f, 0.82f, 0.68f, 1f),
        "[绿]" => new(0.46f, 0.85f, 0.35f, 1f),
        "[橙]" => new(0.95f, 0.52f, 0.25f, 1f),
        "[紫]" => new(0.75f, 0.48f, 0.92f, 1f),
        _ => UiTheme.Text
    };

    private static Vector4 SoulShardTagColor(string tag) => tag switch
    {
        "[游]" => new(0.42f, 0.82f, 0.50f, 1f),
        "[狂]" => new(0.94f, 0.40f, 0.30f, 1f),
        "[预]" => new(0.48f, 0.76f, 1.00f, 1f),
        "[死]" => new(0.72f, 0.48f, 0.90f, 1f),
        "[青魔]" => new(0.32f, 0.66f, 0.96f, 1f),
        _ => UiTheme.Text
    };
}
