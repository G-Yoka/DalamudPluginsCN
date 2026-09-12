using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CrescentCompass.Services;

public sealed class WatchedEventNotificationService : IDisposable
{
    private const long BatchDelayMilliseconds = 1_000;
    private const long MissingGraceMilliseconds = 3_000;
    private const string ToastGroup = "CrescentCompass-WatchedEvents";
    private readonly PluginConfiguration configuration;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly INotificationManager notificationManager;
    private readonly OccultEventTracker eventTracker;
    private readonly NavigationService navigationService;
    private readonly IPluginLog log;
    private readonly EventAppearanceLedger appearances = new();
    private readonly Dictionary<EventAppearanceKey, OccultEventSnapshot> pending = [];
    private readonly CancellationTokenSource disposalCancellation = new();
    private uint territoryId;
    private uint zoneServerId;
    private bool firstScan = true;
    private long flushAt;
    private ProminentBanner? prominentBanner;
    private bool disposed;

    public WatchedEventNotificationService(PluginConfiguration configuration, IClientState clientState,
        IObjectTable objectTable, IFramework framework, INotificationManager notificationManager,
        OccultEventTracker eventTracker, NavigationService navigationService, IPluginLog log)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.notificationManager = notificationManager;
        this.eventTracker = eventTracker;
        this.navigationService = navigationService;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        disposalCancellation.Cancel();
        disposalCancellation.Dispose();
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
        if (!banner.WasHovered) banner.RemainingMilliseconds -= elapsed;
        if (banner.RemainingMilliseconds <= 0)
        {
            prominentBanner = null;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGui.GetMainViewport();
        var width = MathF.Min(620f * scale, viewport.WorkSize.X - 32f * scale);
        var height = (68f + banner.Events.Count * 48f) * scale;
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X + viewport.WorkSize.X / 2f,
            viewport.WorkPos.Y + 28f * scale), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);

        var age = 8_000 - banner.RemainingMilliseconds;
        var pulse = !configuration.ReduceMotion && age < 1_000
            ? 0.82f + 0.18f * MathF.Abs(MathF.Sin(age / 1_000f * MathF.Tau * 2f))
            : 1f;
        var accent = banner.Events.Any(item => item.Kind == OccultEventKind.CriticalEngagement)
            ? new Vector4(1.00f, 0.78f, 0.22f, pulse)
            : new Vector4(0.20f, 0.82f, 0.96f, pulse);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.055f, 0.075f, 0.11f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(accent.X, accent.Y, accent.Z, 0.30f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 10f) * scale);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("###CrescentCompass-ProminentEventAlert", flags))
        {
            ImGui.TextColored(accent, banner.Events.Count == 1
                ? "收藏事件已出现"
                : $"{banner.Events.Count} 个收藏事件已出现");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 34f * scale);
            if (ImGui.SmallButton("×##close-prominent-event")) prominentBanner = null;
            ImGui.Separator();

            foreach (var item in banner.Events)
            {
                ImGui.PushID(unchecked((int)item.DataId) ^ (item.Kind == OccultEventKind.CriticalEngagement ? 0x40000000 : 0));
                var reward = string.IsNullOrEmpty(item.RewardTag) ? string.Empty : $" {item.RewardTag}";
                if (ImGui.Selectable($"{KindName(item)} · {item.Name}{reward}##navigate", false,
                        ImGuiSelectableFlags.None, new Vector2(-1f, 0f)))
                {
                    navigationService.NavigateToEvent(item.Position, $"{KindName(item)}：{item.Name}");
                    prominentBanner = null;
                }
                var distance = objectTable.LocalPlayer is { } player
                    ? $" · {MathF.Sqrt(Vector3.DistanceSquared(player.Position, item.Position)):F0}m"
                    : string.Empty;
                ImGui.TextDisabled($"{item.StateText}{distance}");
                ImGui.PopID();
            }

            banner.WasHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
    }

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
            .Where(item => item.Kind is OccultEventKind.CriticalEngagement or OccultEventKind.Fate or OccultEventKind.MagicPot)
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
        var events = pending.Values.ToArray();
        pending.Clear();
        var target = Nearest(events);
        var title = events.Length == 1 ? "收藏事件已出现" : $"{events.Length} 个收藏事件已出现";
        var content = string.Join('\n', events.Take(5).Select(DisplayLine));
        if (events.Length > 5) content += $"\n另有 {events.Length - 5} 个事件";

        if (IsGameForeground())
        {
            ShowInGame(title, content, target);
            if (configuration.ShowProminentInGameEventNotifications)
                prominentBanner = new ProminentBanner(events.OrderBy(DistanceSquared).ToArray());
        }
        else if (configuration.ShowWindowsEventNotifications && TryShowWindows(title, content)) { }
        else ShowInGame(title, content, target);

        if (configuration.WatchedCeSound) MessageBeep(0x00000040);
    }

    private void ShowInGame(string title, string content, OccultEventSnapshot target)
    {
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

    private bool TryShowWindows(string title, string content)
    {
        try
        {
            var appId = CurrentAppUserModelId();
            var xml = new XmlDocument();
            xml.LoadXml($"<toast duration=\"short\"><visual><binding template=\"ToastGeneric\">" +
                        $"<text>{Escape(title)}</text><text>{Escape(content)}</text>" +
                        "</binding></visual><audio silent=\"true\"/></toast>");
            var tag = $"watched-{Guid.NewGuid():N}";
            var toast = new ToastNotification(xml)
            {
                Tag = tag,
                Group = ToastGroup,
                ExpirationTime = DateTimeOffset.Now.AddSeconds(5)
            };
            if (string.IsNullOrWhiteSpace(appId)) ToastNotificationManager.CreateToastNotifier().Show(toast);
            else ToastNotificationManager.CreateToastNotifier(appId).Show(toast);
            _ = RemoveToastAfterDelay(tag, appId, disposalCancellation.Token);
            return true;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Unable to show Windows watched-event notification; using in-game notification.");
            return false;
        }
    }

    private async Task RemoveToastAfterDelay(string tag, string? appId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(appId)) ToastNotificationManager.History.Remove(tag, ToastGroup);
            else ToastNotificationManager.History.Remove(tag, ToastGroup, appId);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            log.Debug(exception, "Unable to remove Windows watched-event notification history item.");
        }
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

    private static string KindName(OccultEventSnapshot item) =>
        item.Kind == OccultEventKind.CriticalEngagement ? "CE" : "FATE";

    private static bool IsGameForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static string? CurrentAppUserModelId()
    {
        var result = GetCurrentProcessExplicitAppUserModelID(out var pointer);
        if (result != 0 || pointer == 0) return null;
        try { return Marshal.PtrToStringUni(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);

    private static EventAppearanceKey Key(OccultEventSnapshot item) =>
        new(item.Kind == OccultEventKind.CriticalEngagement, item.DataId);

    private sealed class ProminentBanner
    {
        public ProminentBanner(IReadOnlyList<OccultEventSnapshot> events)
        {
            Events = events;
            LastDrawAt = Environment.TickCount64;
        }

        public IReadOnlyList<OccultEventSnapshot> Events { get; }
        public long RemainingMilliseconds { get; set; } = 8_000;
        public long LastDrawAt { get; set; }
        public bool WasHovered { get; set; }
    }
}
