using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CrescentCompass.Services;

public sealed class WatchedEventNotificationService : IDisposable
{
    private const long BatchDelayMilliseconds = 1_000;
    private const long MissingGraceMilliseconds = 3_000;
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly INotificationManager notificationManager;
    private readonly OccultEventTracker eventTracker;
    private readonly NavigationService navigationService;
    private readonly IPluginLog log;
    private readonly Action save;
    private readonly string pluginDirectory;
    private readonly EventAppearanceLedger appearances = new();
    private readonly Dictionary<EventAppearanceKey, OccultEventSnapshot> pending = [];
    private uint territoryId;
    private uint zoneServerId;
    private bool firstScan = true;
    private long flushAt;
    private ProminentBanner? prominentBanner;
    private string windowsNotificationTestStatus = "尚未测试系统通知";
    private bool disposed;

    public WatchedEventNotificationService(PluginConfiguration configuration, IClientState clientState,
        IObjectTable objectTable, IFramework framework, IDataManager dataManager,
        INotificationManager notificationManager, OccultEventTracker eventTracker,
        NavigationService navigationService, IPluginLog log, Action save, string pluginDirectory)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.dataManager = dataManager;
        this.notificationManager = notificationManager;
        this.eventTracker = eventTracker;
        this.navigationService = navigationService;
        this.log = log;
        this.save = save;
        this.pluginDirectory = pluginDirectory;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        pending.Clear();
        appearances.Reset();
        prominentBanner = null;
    }

    public void Draw()
    {
        if (!configuration.ShowProminentInGameEventNotifications || prominentBanner is not { } banner)
        {
            prominentBanner = null;
            return;
        }

        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - banner.LastDrawAt, 0, 100);
        banner.LastDrawAt = now;
        if (!configuration.PauseProminentBannerOnHover || !banner.WasHovered)
            banner.RemainingMilliseconds -= elapsed;
        if (banner.RemainingMilliseconds <= 0)
        {
            prominentBanner = null;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGui.GetMainViewport();
        var width = MathF.Min(configuration.ProminentBannerWidth * scale, viewport.WorkSize.X - 32f * scale);
        var estimatedRowHeight = configuration.ProminentBannerDetail switch
        {
            EventBannerDetail.Compact => 68f,
            EventBannerDetail.Detailed => 128f,
            _ => 90f
        };
        var estimatedHeight = (58f + banner.Events.Count * estimatedRowHeight +
                               Math.Max(0, banner.Events.Count - 1) * 7f) * scale;
        var layoutHeight = banner.LastHeight > 0f ? banner.LastHeight : estimatedHeight;
        var (position, pivot) = BannerPlacement(viewport, scale, width, layoutHeight);
        ImGui.SetNextWindowPos(position,
            configuration.ProminentBannerPosition == EventBannerPosition.Custom ? ImGuiCond.Appearing : ImGuiCond.Always,
            pivot);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, float.MaxValue));

        var age = banner.DurationMilliseconds - banner.RemainingMilliseconds;
        var pulse = !configuration.ReduceMotion && age < 1_000
            ? 0.82f + 0.18f * MathF.Abs(MathF.Sin(age / 1_000f * MathF.Tau * 2f))
            : 1f;
        var accent = banner.Events.Any(item => item.Kind == OccultEventKind.CriticalEngagement)
            ? new Vector4(1.00f, 0.78f, 0.22f, pulse)
            : new Vector4(0.20f, 0.82f, 0.96f, pulse);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,
            new Vector4(0.055f, 0.075f, 0.11f, configuration.ProminentBannerOpacity));
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(accent.X, accent.Y, accent.Z, 0.30f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 10f) * scale);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;
        if (configuration.ProminentBannerPosition == EventBannerPosition.Custom)
            flags &= ~ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("###CrescentCompass-ProminentEventAlert", flags))
        {
            ImGui.TextColored(accent, banner.Events.Count == 1
                ? $"收藏 {KindName(banner.Events[0])} 已出现"
                : $"{banner.Events.Count} 个收藏事件已出现");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 34f * scale);
            if (ImGui.SmallButton("×##close-prominent-event")) prominentBanner = null;
            ImGui.Separator();

            foreach (var item in banner.Events)
            {
                ImGui.PushID(unchecked((int)item.DataId) ^ (item.Kind == OccultEventKind.CriticalEngagement ? 0x40000000 : 0));
                DrawBannerEvent(item);
                ImGui.PopID();
                if (!item.Equals(banner.Events[^1])) ImGui.Separator();
            }

            banner.WasHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            banner.LastHeight = ImGui.GetWindowHeight();
            if (configuration.ProminentBannerPosition == EventBannerPosition.Custom &&
                banner.WasHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                var windowPosition = ImGui.GetWindowPos();
                configuration.ProminentBannerCustomX = Math.Clamp(
                    (windowPosition.X - viewport.WorkPos.X) / Math.Max(1f, viewport.WorkSize.X - width), 0f, 1f);
                configuration.ProminentBannerCustomY = Math.Clamp(
                    (windowPosition.Y - viewport.WorkPos.Y) /
                    Math.Max(1f, viewport.WorkSize.Y - banner.LastHeight), 0f, 1f);
                save();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
    }

    public void ShowPreview()
    {
        var origin = objectTable.LocalPlayer?.Position ?? Vector3.Zero;
        prominentBanner = new ProminentBanner(
        [
            new OccultEventSnapshot(35, "愤怒的人造人——新月狂战士", origin + new Vector3(112f, 1f, 46f), 0,
                OccultEventKind.CriticalEngagement, "报名中 · 剩余 01:46", "[青]", true),
            new OccultEventSnapshot(1971, "骄傲的咒杀者——执行者", origin + new Vector3(168f, 3f, 72f), 0,
                OccultEventKind.Fate, "进度 12% · 剩余 19:12", "[黄]", true)
        ], BannerDurationMilliseconds());
        PlayInGameNotificationSound();
    }

    public string WindowsNotificationTestStatus => windowsNotificationTestStatus;

    public void TestInGameNotificationSound() => PlayInGameNotificationSound();

    public void TestWindowsNotification()
    {
        TryShowWindows(
            "魔法罐预告 · 幸福的魔法罐（上）",
            "预计 01:04:22 出现 · 还有 04:27\n新月岛南部 · X:24.6 Y:34.8 · 众包",
            "新月罗盘 · 伪数据预览");
    }

    private void DrawBannerEvent(OccultEventSnapshot item)
    {
        var kind = KindName(item);
        var name = ResolveName(item);
        if (!configuration.ShowProminentBannerNavigateButton)
        {
            DrawBannerEventContent(item, kind, name);
            return;
        }

        if (!ImGui.BeginTable("###banner-event-layout", 2, ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 54f * ImGuiHelpers.GlobalScale);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var contentTop = ImGui.GetCursorPosY();
        DrawBannerEventContent(item, kind, name);
        var contentBottom = ImGui.GetCursorPosY();

        ImGui.TableSetColumnIndex(1);
        var buttonHeight = ImGui.GetFrameHeight();
        ImGui.SetCursorPosY(contentTop + MathF.Max(0f, (contentBottom - contentTop - buttonHeight) / 2f));
        if (ImGui.SmallButton("前往##banner-navigate"))
        {
            navigationService.NavigateToEvent(item.Position, $"{kind}：{name}");
            prominentBanner = null;
        }
        ImGui.EndTable();
    }

    private void DrawBannerEventContent(OccultEventSnapshot item, string kind, string name)
    {
        var lineStart = ImGui.GetCursorPosX();
        ImGui.TextColored(item.Kind == OccultEventKind.CriticalEngagement
            ? new Vector4(1f, 0.78f, 0.22f, 1f)
            : new Vector4(0.20f, 0.82f, 0.96f, 1f), kind);
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorPosX(lineStart + 42f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(name);
        DrawRewardTags(item);

        ImGui.TextDisabled(item.StateText);
        if (configuration.ProminentBannerDetail >= EventBannerDetail.Standard)
            DrawBannerLocation(item);
        if (configuration.ProminentBannerDetail == EventBannerDetail.Detailed)
            DrawBannerDetails(item);
    }

    private void DrawRewardTags(OccultEventSnapshot item)
    {
        if (!string.IsNullOrWhiteSpace(item.RewardTag))
        {
            ImGui.SameLine(0f, 5f * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(RewardTagColor(item.RewardTag), item.RewardTag);
        }
        if (!OccultEventRewardCatalog.TryGetSoulShard(EventTerritory(item), item.DataId, out var soulShard)) return;
        ImGui.SameLine(0f, 5f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(SoulShardTagColor(soulShard.Tag), soulShard.Tag);
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"灵魂碎晶：{soulShard.JobName}");
        ImGui.EndTooltip();
    }

    private void DrawBannerLocation(OccultEventSnapshot item)
    {
        var parts = new List<string>();
        if (TryWorldToMap(item.Position, out var mapPosition))
            parts.Add($"X:{mapPosition.X:F1} Y:{mapPosition.Y:F1}");
        if (objectTable.LocalPlayer is { } player)
        {
            var dx = player.Position.X - item.Position.X;
            var dz = player.Position.Z - item.Position.Z;
            parts.Add($"距离 {MathF.Sqrt(dx * dx + dz * dz):F0}m");
            var height = item.Position.Y - player.Position.Y;
            parts.Add($"高差 {height:+0;-0;0}m");
        }
        if (parts.Count > 0) ImGui.TextDisabled(string.Join(" · ", parts));
    }

    private void DrawBannerDetails(OccultEventSnapshot item)
    {
        ImGui.TextDisabled($"事件 ID {item.DataId}");
        if (item.Kind != OccultEventKind.CriticalEngagement) return;
        var eventTerritory = EventTerritory(item);
        var definition = CeSpawnCatalog.All.FirstOrDefault(entry =>
            entry.Id == item.DataId && entry.TerritoryId == eventTerritory);
        if (definition == null) return;
        if (definition.MobNameId == 0)
        {
            ImGui.TextDisabled("触发：自动出现");
            return;
        }
        var mobName = dataManager.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>()
            .GetRowOrDefault(definition.MobNameId)?.Singular.ToString();
        if (string.IsNullOrWhiteSpace(mobName)) mobName = definition.MobFallback;
        var acceleration = definition.CanSpawnNaturally ? " · 可自然出现／击杀可加速" : string.Empty;
        ImGui.TextDisabled($"触发：{mobName} (Lv.{definition.Level}){acceleration}");
    }

    private string ResolveName(OccultEventSnapshot item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name)) return item.Name;
        if (item.Kind == OccultEventKind.CriticalEngagement)
        {
            var localized = dataManager.GetExcelSheet<Lumina.Excel.Sheets.DynamicEvent>()
                .GetRowOrDefault(item.DataId)?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(localized)) return localized;
            return CeSpawnCatalog.All.FirstOrDefault(entry => entry.Id == item.DataId)?.EnglishName ?? $"CE #{item.DataId}";
        }
        var fateName = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Fate>()
            .GetRowOrDefault(item.DataId)?.Name.ToString();
        if (!string.IsNullOrWhiteSpace(fateName)) return fateName;
        return FateCatalog.All.FirstOrDefault(entry => entry.Id == item.DataId)?.EnglishName ?? $"FATE #{item.DataId}";
    }

    private uint EventTerritory(OccultEventSnapshot item)
    {
        if (territoryId != 0) return territoryId;
        if (item.Kind == OccultEventKind.CriticalEngagement)
            return CeSpawnCatalog.All.FirstOrDefault(entry => entry.Id == item.DataId)?.TerritoryId ?? 0;
        return FateCatalog.All.FirstOrDefault(entry => entry.Id == item.DataId)?.TerritoryId ?? 0;
    }

    private bool TryWorldToMap(Vector3 position, out Vector2 mapPosition)
    {
        mapPosition = default;
        var map = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>().GetRowOrDefault(clientState.MapId);
        if (map is not { } row || row.SizeFactor == 0) return false;
        var scale = row.SizeFactor / 100f;
        var texture = (new Vector2(position.X, position.Z) + new Vector2(row.OffsetX, row.OffsetY)) * scale
                      + new Vector2(1024f);
        mapPosition = texture / 2048f * 40.96f / scale + Vector2.One;
        return float.IsFinite(mapPosition.X) && float.IsFinite(mapPosition.Y);
    }

    private (Vector2 Position, Vector2 Pivot) BannerPlacement(
        ImGuiViewportPtr viewport, float scale, float width, float height)
    {
        const float margin = 28f;
        return configuration.ProminentBannerPosition switch
        {
            EventBannerPosition.TopLeft =>
                (viewport.WorkPos + new Vector2(16f * scale, margin * scale), Vector2.Zero),
            EventBannerPosition.TopRight =>
                (viewport.WorkPos + new Vector2(viewport.WorkSize.X - 16f * scale, margin * scale), new Vector2(1f, 0f)),
            EventBannerPosition.Custom =>
                (viewport.WorkPos + new Vector2(
                    configuration.ProminentBannerCustomX * Math.Max(1f, viewport.WorkSize.X - width),
                    configuration.ProminentBannerCustomY * Math.Max(1f, viewport.WorkSize.Y - height)), Vector2.Zero),
            _ =>
                (viewport.WorkPos + new Vector2(viewport.WorkSize.X / 2f, margin * scale), new Vector2(0.5f, 0f))
        };
    }

    private long BannerDurationMilliseconds() =>
        (long)MathF.Round(configuration.ProminentBannerDurationSeconds * 1_000f);

    private static Vector4 RewardTagColor(string tag) => tag switch
    {
        "[黄]" => new(0.96f, 0.83f, 0.37f, 1f),
        "[青]" => new(0.35f, 0.84f, 0.90f, 1f),
        "[碧]" => new(0.34f, 0.82f, 0.68f, 1f),
        "[绿]" => new(0.46f, 0.85f, 0.35f, 1f),
        "[橙]" => new(0.95f, 0.52f, 0.25f, 1f),
        "[紫]" => new(0.75f, 0.48f, 0.92f, 1f),
        _ => new(0.85f, 0.89f, 0.94f, 1f)
    };

    private static Vector4 SoulShardTagColor(string tag) => tag switch
    {
        "[游]" => new(0.42f, 0.82f, 0.50f, 1f),
        "[狂]" => new(0.94f, 0.40f, 0.30f, 1f),
        "[预]" => new(0.48f, 0.76f, 1.00f, 1f),
        "[死]" => new(0.72f, 0.48f, 0.90f, 1f),
        "[青魔]" => new(0.32f, 0.66f, 0.96f, 1f),
        _ => new(0.85f, 0.89f, 0.94f, 1f)
    };

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        var currentTerritory = clientState.TerritoryType;
        var supported = currentTerritory is Core.PotCandidateCatalog.SouthHornTerritoryId or
            Core.PotCandidateCatalog.NorthHornTerritoryId;
        if (!supported)
        {
            if (territoryId != 0) ResetContext(0, 0);
            return;
        }

        var currentZoneServerId = eventTracker.ZoneServerId;
        if (territoryId != currentTerritory ||
            zoneServerId != 0 && currentZoneServerId != 0 && zoneServerId != currentZoneServerId)
            ResetContext(currentTerritory, currentZoneServerId);
        else if (zoneServerId == 0 && currentZoneServerId != 0)
            zoneServerId = currentZoneServerId;

        var actualEvents = eventTracker.ActiveEvents
            .Where(item => item.Kind is OccultEventKind.CriticalEngagement or OccultEventKind.Fate or
                OccultEventKind.MagicPot or OccultEventKind.MagicPotForecast)
            .ToArray();
        var notifyThisScan = !firstScan || configuration.NotifyCeOnEntry;

        foreach (var item in actualEvents)
        {
            var key = Key(item);
            var newlySeen = appearances.Observe(key, now);
            if (!newlySeen || !notifyThisScan || !configuration.NotifyWatchedCe || !IsWatched(item) ||
                item.Kind == OccultEventKind.CriticalEngagement && !item.IsJoinable)
                continue;
            pending[key] = item;
            if (flushAt == 0) flushAt = now + BatchDelayMilliseconds;
        }

        appearances.Prune(now, MissingGraceMilliseconds);

        firstScan = false;
        if (flushAt != 0 && now >= flushAt) Flush();
    }

    private void ResetContext(uint newTerritoryId, uint newZoneServerId)
    {
        territoryId = newTerritoryId;
        zoneServerId = newZoneServerId;
        firstScan = true;
        flushAt = 0;
        pending.Clear();
        appearances.Reset();
    }

    private bool IsWatched(OccultEventSnapshot item) => item.Kind == OccultEventKind.CriticalEngagement
        ? configuration.WatchedCeIds.Contains(item.DataId)
        : configuration.WatchedFateIds.Contains(item.DataId);

    private void Flush()
    {
        flushAt = 0;
        if (pending.Count == 0) return;
        var activeEvents = eventTracker.ActiveEvents;
        var events = pending.Values
            .Select(item =>
            {
                var latest = activeEvents.FirstOrDefault(active => Key(active) == Key(item));
                return latest.DataId != 0 ? latest : item;
            })
            .Select(item => item with { Name = ResolveName(item) })
            .ToArray();
        pending.Clear();
        var target = Nearest(events);
        var title = events.Length == 1
            ? $"收藏 {KindName(events[0])} 已出现"
            : $"{events.Length} 个收藏事件已出现";
        var content = string.Join('\n', events.Take(5).Select(DisplayLine));
        if (events.Length > 5) content += $"\n另有 {events.Length - 5} 个事件";

        if (configuration.ShowProminentInGameEventNotifications)
            prominentBanner = new ProminentBanner(events.OrderBy(DistanceSquared).ToArray(),
                BannerDurationMilliseconds());

        if (IsGameForeground())
        {
            ShowInGame(title, content, target);
        }
        else if (configuration.ShowWindowsEventNotifications && TryShowWindows(title, content)) { }
        else ShowInGame(title, content, target);

    }

    private void ShowInGame(string title, string content, OccultEventSnapshot target)
    {
        PlayInGameNotificationSound();
        var active = notificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
        {
            Title = title,
            Content = content,
            Type = NotificationType.Info,
            InitialDuration = TimeSpan.FromSeconds(8),
            ExtensionDurationSinceLastInterest = TimeSpan.FromSeconds(3),
            RespectUiHidden = false,
            Minimized = false
        });
        active.Click += args =>
        {
            navigationService.NavigateToEvent(target.Position, $"{KindName(target)}：{target.Name}");
            args.Notification.DismissNow();
        };
    }

    private unsafe void PlayInGameNotificationSound()
    {
        if (!configuration.WatchedCeSound) return;
        try
        {
            UIGlobals.PlayChatSoundEffect((uint)configuration.EventNotificationSoundEffect);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Unable to play the watched-event in-game notification sound.");
        }
    }

    private bool TryShowWindows(string title, string content, string attribution = "新月罗盘")
    {
        try
        {
            var systemDirectory = Environment.SystemDirectory;
            if (string.IsNullOrWhiteSpace(systemDirectory))
            {
                var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR");
                if (string.IsNullOrWhiteSpace(windowsDirectory))
                    throw new DirectoryNotFoundException("无法确定 Windows 系统目录。");
                systemDirectory = Path.Combine(windowsDirectory, "System32");
            }
            var powershell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell))
                throw new FileNotFoundException("找不到 Windows PowerShell，无法创建隔离的通知进程。", powershell);

            var helper = ResolveNotificationAsset("scripts", "ToastHelper.ps1");
            var iconPng = ResolveNotificationAsset("images", "toast-icon.png");
            var iconIco = ResolveNotificationAsset("images", "icon.ico");
            var encodedTitle = Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
            var encodedContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            var encodedAttribution = Convert.ToBase64String(Encoding.UTF8.GetBytes(attribution));
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(helper);
            startInfo.ArgumentList.Add("-TitleBase64");
            startInfo.ArgumentList.Add(encodedTitle);
            startInfo.ArgumentList.Add("-ContentBase64");
            startInfo.ArgumentList.Add(encodedContent);
            startInfo.ArgumentList.Add("-AttributionBase64");
            startInfo.ArgumentList.Add(encodedAttribution);
            startInfo.ArgumentList.Add("-IconPng");
            startInfo.ArgumentList.Add(iconPng);
            startInfo.ArgumentList.Add("-IconIco");
            startInfo.ArgumentList.Add(iconIco);
            var process = Process.Start(startInfo) ??
                          throw new InvalidOperationException("Windows PowerShell 通知进程未能启动。");
            windowsNotificationTestStatus = "系统通知已交给独立进程发送…";
            _ = ObserveToastProcess(process);
            return true;
        }
        catch (Exception exception)
        {
            windowsNotificationTestStatus = $"系统通知发送失败：{exception.GetType().Name} · {exception.Message}";
            log.Warning(exception, "Unable to show Windows watched-event notification; using in-game notification.");
            return false;
        }
    }

    private string ResolveNotificationAsset(params string[] relativeParts)
    {
        var roots = new[]
        {
            pluginDirectory,
            Path.GetDirectoryName(typeof(WatchedEventNotificationService).Assembly.Location),
            AppContext.BaseDirectory
        }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase);

        string? firstCandidate = null;
        foreach (var root in roots)
        {
            var candidate = relativeParts.Aggregate(root!, Path.Combine);
            firstCandidate ??= candidate;
            if (File.Exists(candidate)) return candidate;

            var parent = Directory.GetParent(root!)?.FullName;
            if (string.IsNullOrWhiteSpace(parent)) continue;
            candidate = relativeParts.Aggregate(parent, Path.Combine);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"找不到系统通知资源：{Path.Combine(relativeParts)}", firstCandidate);
    }

    private async Task ObserveToastProcess(Process process)
    {
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            windowsNotificationTestStatus = process.ExitCode == 0
                ? "系统通知发送成功。若未显示，请检查 Windows 通知与勿扰模式。"
                : $"系统通知发送失败：PowerShell 退出代码 {process.ExitCode}" +
                  (error.Length == 0 ? string.Empty : $" · {error}");
        }
        catch (Exception exception)
        {
            windowsNotificationTestStatus = $"系统通知进程失败：{exception.GetType().Name} · {exception.Message}";
            log.Warning(exception, "Unable to observe Windows notification helper process.");
        }
        finally { process.Dispose(); }
    }

    private OccultEventSnapshot Nearest(IReadOnlyList<OccultEventSnapshot> events)
    {
        if (objectTable.LocalPlayer is not { } player) return events[0];
        return events.MinBy(item => Vector3.DistanceSquared(player.Position, item.Position));
    }

    private float DistanceSquared(OccultEventSnapshot item) => objectTable.LocalPlayer is { } player
        ? Vector3.DistanceSquared(player.Position, item.Position)
        : 0f;

    private static string DisplayLine(OccultEventSnapshot item)
    {
        var reward = string.IsNullOrEmpty(item.RewardTag) ? string.Empty : $" {item.RewardTag}";
        return $"{KindName(item)}：{item.Name}{reward} · {item.StateText}";
    }

    private static string KindName(OccultEventSnapshot item) => item.Kind switch
    {
        OccultEventKind.CriticalEngagement => "CE",
        OccultEventKind.MagicPotForecast => "魔法罐预告",
        OccultEventKind.MagicPot => "魔法罐",
        _ => "FATE"
    };

    private static bool IsGameForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private static EventAppearanceKey Key(OccultEventSnapshot item) =>
        new((byte)item.Kind, item.DataId);

    private sealed class ProminentBanner
    {
        public ProminentBanner(IReadOnlyList<OccultEventSnapshot> events, long durationMilliseconds)
        {
            Events = events;
            LastDrawAt = Environment.TickCount64;
            DurationMilliseconds = durationMilliseconds;
            RemainingMilliseconds = durationMilliseconds;
        }

        public IReadOnlyList<OccultEventSnapshot> Events { get; }
        public long DurationMilliseconds { get; }
        public long RemainingMilliseconds { get; set; }
        public long LastDrawAt { get; set; }
        public bool WasHovered { get; set; }
        public float LastHeight { get; set; }
    }
}
