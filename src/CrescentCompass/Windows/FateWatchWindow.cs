using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FateRow = Lumina.Excel.Sheets.Fate;

namespace CrescentCompass.Windows;

public sealed class FateWatchWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly OccultEventTracker eventTracker;
    private readonly NavigationService navigationService;
    private readonly Action save;
    private readonly Dictionary<uint, string> localizedNames = [];
    private string search = string.Empty;
    private int islandFilter;
    private bool onlyFavorites;

    public FateWatchWindow(PluginConfiguration configuration, IClientState clientState,
        IDataManager dataManager, OccultEventTracker eventTracker,
        NavigationService navigationService, Action save)
        : base("FATE 关注与位置速查###CrescentCompass-FateWatch")
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
        ImGui.InputTextWithHint("###CrescentCompass-FateSearch", "搜索 FATE 或 ID", ref search, 100);
        ImGui.SameLine();
        ImGui.Checkbox("仅收藏", ref onlyFavorites);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        ImGui.Combo("###CrescentCompass-FateIslandFilter", ref islandFilter, "全部\0南部\0北部\0");

        var territory = clientState.TerritoryType;
        var entries = FateCatalog.All.Where(Matches).ToArray();
        ImGui.TextDisabled($"{entries.Length} / {FateCatalog.All.Count} 项 · 当前区域 {AreaName(territory)}");
        ImGui.Separator();

        if (entries.Length == 0)
        {
            ImGui.TextDisabled("没有符合当前筛选条件的 FATE。");
            if (ImGui.Button("清除筛选"))
            {
                search = string.Empty;
                islandFilter = 0;
                onlyFavorites = false;
            }
            UiTheme.PopContentStyle(colors);
            return;
        }

        if (!ImGui.BeginTable("###CrescentCompass-FateTable", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingStretchProp))
        {
            UiTheme.PopContentStyle(colors);
            return;
        }
        ImGui.TableSetupColumn("关注", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("区域", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("FATE", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("位置", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            var active = entry.TerritoryId == territory
                ? eventTracker.ActiveEvents.FirstOrDefault(item =>
                    item.DataId == entry.Id && item.Kind is OccultEventKind.Fate or OccultEventKind.MagicPot or OccultEventKind.MagicPotForecast)
                : default;
            var name = Name(entry);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var watched = configuration.WatchedFateIds.Contains(entry.Id);
            if (ImGui.SmallButton($"{(watched ? "★" : "☆")}##watch-fate-{entry.Id}"))
            {
                if (watched) configuration.WatchedFateIds.Remove(entry.Id);
                else configuration.WatchedFateIds.Add(entry.Id);
                save();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.TerritoryId == PotCandidateCatalog.SouthHornTerritoryId ? "南部" : "北部");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{name}  #{entry.Id}");
            var rewardTag = !string.IsNullOrEmpty(active.RewardTag)
                ? active.RewardTag
                : OccultEventRewardCatalog.FateTag(entry.TerritoryId, entry.Id);
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
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(active.DataId != 0
                ? active.StateText
                : entry.TerritoryId == territory ? "尚未出现" : "未在对应岛内");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"X:{entry.MapPosition.X:F1} Y:{entry.MapPosition.Y:F1}");
            ImGui.TableNextColumn();
            var canNavigate = entry.TerritoryId == territory;
            if (!canNavigate) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"前往##navigate-fate-{entry.Id}"))
            {
                if (active.DataId != 0)
                    navigationService.NavigateToEvent(active.Position, $"FATE：{name}");
                else
                    navigationService.NavigateToMapPosition(entry.MapPosition, $"FATE：{name}");
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

    private bool Matches(FateDefinition entry)
    {
        if (islandFilter == 1 && entry.TerritoryId != PotCandidateCatalog.SouthHornTerritoryId) return false;
        if (islandFilter == 2 && entry.TerritoryId != PotCandidateCatalog.NorthHornTerritoryId) return false;
        if (onlyFavorites && !configuration.WatchedFateIds.Contains(entry.Id)) return false;
        var term = search.Trim();
        var name = Name(entry);
        return term.Length == 0 || name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase) || entry.Id.ToString().Contains(term);
    }

    private string Name(FateDefinition entry)
    {
        if (localizedNames.TryGetValue(entry.Id, out var name)) return name;
        var localized = dataManager.GetExcelSheet<FateRow>().GetRowOrDefault(entry.Id)?.Name.ToString();
        name = string.IsNullOrWhiteSpace(localized) ? entry.EnglishName : localized;
        localizedNames[entry.Id] = name;
        return name;
    }

    private static string AreaName(uint territory) => territory switch
    {
        PotCandidateCatalog.SouthHornTerritoryId => "南部",
        PotCandidateCatalog.NorthHornTerritoryId => "北部",
        _ => "区域外"
    };

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
}
