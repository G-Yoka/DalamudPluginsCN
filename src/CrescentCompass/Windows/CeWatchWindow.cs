using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using BNpcName = Lumina.Excel.Sheets.BNpcName;
using DynamicEvent = Lumina.Excel.Sheets.DynamicEvent;

namespace CrescentCompass.Windows;

public sealed class CeWatchWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly OccultEventTracker eventTracker;
    private readonly NavigationService navigationService;
    private readonly Action save;
    private readonly Dictionary<uint, (string Event, string Mob)> localizedLabels = [];
    private string search = string.Empty;
    private int islandFilter;
    private bool onlyFavorites;

    public CeWatchWindow(
        PluginConfiguration configuration,
        IClientState clientState,
        IDataManager dataManager,
        OccultEventTracker eventTracker,
        NavigationService navigationService,
        Action save)
        : base("CE 关注与触发怪速查###CrescentCompass-CeWatch")
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.eventTracker = eventTracker;
        this.navigationService = navigationService;
        this.save = save;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(560f, 360f),
            MaximumSize = new(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        var colors = UiTheme.PushContentStyle(configuration.ComfortableUiDensity);
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("###CrescentCompass-CeSearch", "搜索事件、触发怪或 ID", ref search, 100);
        ImGui.SameLine();
        ImGui.Checkbox("仅收藏", ref onlyFavorites);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        ImGui.Combo("###CrescentCompass-IslandFilter", ref islandFilter, "全部\0南部\0北部\0");

        var territory = clientState.TerritoryType;
        var entries = CeSpawnCatalog.All.Where(Matches).ToArray();
        ImGui.TextDisabled($"{entries.Length} / {CeSpawnCatalog.All.Count} 项 · 当前区域 {AreaName(territory)}");
        ImGui.Separator();

        if (entries.Length == 0)
        {
            ImGui.TextDisabled("没有符合当前筛选条件的事件。");
            if (ImGui.Button("清除筛选"))
            {
                search = string.Empty;
                islandFilter = 0;
                onlyFavorites = false;
            }
            UiTheme.PopContentStyle(colors);
            return;
        }

        if (!ImGui.BeginTable("###CrescentCompass-CeTable", 7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
        {
            UiTheme.PopContentStyle(colors);
            return;
        }
        ImGui.TableSetupColumn("关注", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("区域", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("CE", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("触发怪", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("寻找位置", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var watched = configuration.WatchedCeIds.Contains(entry.Id);
            if (ImGui.SmallButton($"{(watched ? "★" : "☆")}##watch-{entry.Id}"))
            {
                if (!watched) configuration.WatchedCeIds.Add(entry.Id);
                else configuration.WatchedCeIds.Remove(entry.Id);
                save();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.TerritoryId == PotCandidateCatalog.SouthHornTerritoryId ? "南部" : "北部");
            ImGui.TableNextColumn();
            var labels = Labels(entry);
            var active = entry.TerritoryId == territory
                ? eventTracker.ActiveEvents.FirstOrDefault(item =>
                    item.Kind == OccultEventKind.CriticalEngagement && item.DataId == entry.Id)
                : default;
            var rewardTag = !string.IsNullOrEmpty(active.RewardTag)
                ? active.RewardTag
                : OccultEventRewardCatalog.CriticalEncounterTag(entry.TerritoryId, entry.Id);
            ImGui.TextUnformatted($"{labels.Event}  #{entry.Id}");
            if (!string.IsNullOrEmpty(rewardTag))
            {
                ImGui.SameLine(0f, 4f);
                ImGui.TextColored(RewardTagColor(rewardTag), rewardTag);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("半魂晶掉落颜色");
                    ImGui.EndTooltip();
                }
            }
            if (OccultEventRewardCatalog.TryGetSoulShard(entry.TerritoryId, entry.Id, out var soulShard))
            {
                ImGui.SameLine(0f, 4f);
                ImGui.TextColored(SoulShardTagColor(soulShard.Tag), soulShard.Tag);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"灵魂碎晶：{soulShard.JobName}");
                    ImGui.EndTooltip();
                }
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(EventStatus(entry, active, territory));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.MobNameId == 0
                ? "自动出现"
                : $"{labels.Mob} (Lv.{entry.Level}){(entry.CanSpawnNaturally ? " [加速]" : string.Empty)}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.MobNameId == 0 ? "—" : $"X:{entry.SearchPosition.X:F0} Y:{entry.SearchPosition.Y:F0}");
            ImGui.TableNextColumn();
            var canNavigate = entry.TerritoryId == territory &&
                              (active.DataId != 0 || entry.MobNameId != 0);
            if (!canNavigate) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"前往##navigate-{entry.Id}"))
            {
                if (active.DataId != 0)
                    navigationService.NavigateToEvent(active.Position, $"CE：{labels.Event}", active.DataId,
                        CustomNavigationRouteKind.CriticalEngagement);
                else
                    navigationService.NavigateToMapPosition(entry.SearchPosition, $"触发怪：{labels.Mob}");
            }
            if (!canNavigate) ImGui.EndDisabled();
        }

        ImGui.EndTable();
        UiTheme.PopContentStyle(colors);
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
        if (islandFilter == 1 && entry.TerritoryId != PotCandidateCatalog.SouthHornTerritoryId) return false;
        if (islandFilter == 2 && entry.TerritoryId != PotCandidateCatalog.NorthHornTerritoryId) return false;
        if (onlyFavorites && !configuration.WatchedCeIds.Contains(entry.Id)) return false;
        var term = search.Trim();
        var labels = Labels(entry);
        return term.Length == 0 || labels.Event.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               labels.Mob.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.MobFallback.Contains(term, StringComparison.OrdinalIgnoreCase) || entry.Id.ToString().Contains(term) ||
               entry.MobNameId.ToString().Contains(term);
    }

    private (string Event, string Mob) Labels(CeSpawnDefinition entry)
    {
        if (localizedLabels.TryGetValue(entry.Id, out var labels)) return labels;
        var eventName = dataManager.GetExcelSheet<DynamicEvent>().GetRowOrDefault(entry.Id)?.Name.ToString();
        var mobName = entry.MobNameId == 0
            ? string.Empty
            : dataManager.GetExcelSheet<BNpcName>().GetRowOrDefault(entry.MobNameId)?.Singular.ToString();
        labels = (
            string.IsNullOrWhiteSpace(eventName) ? entry.EnglishName : eventName,
            string.IsNullOrWhiteSpace(mobName) ? entry.MobFallback : mobName);
        localizedLabels[entry.Id] = labels;
        return labels;
    }

    private static string AreaName(uint territory) => territory switch
    {
        PotCandidateCatalog.SouthHornTerritoryId => "南部",
        PotCandidateCatalog.NorthHornTerritoryId => "北部",
        _ => "区域外"
    };

    private string EventStatus(CeSpawnDefinition entry, OccultEventSnapshot active, uint territory)
    {
        if (active.DataId != 0) return active.StateText;
        if (entry.TerritoryId != territory) return "未在对应岛内";
        if (entry.MobNameId == 0) return "尚未出现";
        if (!eventTracker.TryGetCeSpawnObservation(entry.Id, out var observation)) return "冷却未知";

        const long cooldownSeconds = 60 * 60;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var availableAt = observation.SpawnedAt + cooldownSeconds;
        var remaining = availableAt - now;
        if (remaining <= 0) return $"冷却结束 · 可触发 · {observation.Source}";
        var localTime = DateTimeOffset.FromUnixTimeSeconds(availableAt).ToLocalTime();
        return $"触发解禁 {localTime:HH:mm:ss} · {observation.Source}";
    }

    private static System.Numerics.Vector4 RewardTagColor(string tag) => tag switch
    {
        "[黄]" => new(0.96f, 0.83f, 0.37f, 1f),
        "[青]" => new(0.35f, 0.84f, 0.90f, 1f),
        "[碧]" => new(0.34f, 0.82f, 0.68f, 1f),
        "[绿]" => new(0.46f, 0.85f, 0.35f, 1f),
        "[橙]" => new(0.95f, 0.52f, 0.25f, 1f),
        "[紫]" => new(0.75f, 0.48f, 0.92f, 1f),
        _ => UiTheme.Text
    };

    private static System.Numerics.Vector4 SoulShardTagColor(string tag) => tag switch
    {
        "[游]" => new(0.42f, 0.82f, 0.50f, 1f),
        "[狂]" => new(0.94f, 0.40f, 0.30f, 1f),
        "[预]" => new(0.48f, 0.76f, 1.00f, 1f),
        "[死]" => new(0.72f, 0.48f, 0.90f, 1f),
        "[青魔]" => new(0.32f, 0.66f, 0.96f, 1f),
        _ => UiTheme.Text
    };
}
