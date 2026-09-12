using CrescentCompass.Configuration;
using CrescentCompass.Integrations;
using CrescentCompass.Services;
using CrescentCompass.Rendering;
using CrescentCompass.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CrescentCompass;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/crescent";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly WindowSystem windows = new("CrescentCompass");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly TreasureTracker treasureTracker;
    private readonly OccultEventTracker occultEventTracker;
    private readonly NavigationService navigationService;
    private readonly TreasureSurveyService treasureSurveyService;
    private readonly WatchedEventNotificationService watchedEventNotificationService;
    private readonly CeWatchWindow ceWatchWindow;
    private readonly FateWatchWindow fateWatchWindow;
    private readonly TreasureMapWindow treasureMapWindow;
    private readonly MapDetailsWindow mapDetailsWindow;
    private readonly SceneOverlayRenderer sceneOverlayRenderer;
    private bool disposed;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        ICondition condition,
        IPartyList partyList,
        IPlayerState playerState,
        IFateTable fateTable,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        INotificationManager notificationManager,
        ISigScanner sigScanner,
        IKeyState keyState,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;

        Configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        Configuration.Normalize();
        pluginInterface.SavePluginConfig(Configuration);
        treasureTracker = new TreasureTracker(
            Configuration, chatGui, clientState, objectTable, framework, dataManager, log, SaveConfiguration);
        var zoneServerIdReader = new ZoneServerIdReader(sigScanner, log);
        occultEventTracker = new OccultEventTracker(
            fateTable, clientState, dataManager, framework, playerState, zoneServerIdReader);
        var vnavmesh = new VNavmeshIpc(pluginInterface);
        navigationService = new NavigationService(
            Configuration, treasureTracker, vnavmesh, keyState, framework,
            objectTable, condition, commandManager, dataManager, SaveConfiguration);
        treasureSurveyService = new TreasureSurveyService(
            Configuration, treasureTracker, clientState, condition, framework, addonLifecycle, log);
        watchedEventNotificationService = new WatchedEventNotificationService(
            Configuration, clientState, objectTable, framework, notificationManager,
            occultEventTracker, navigationService, log);
        sceneOverlayRenderer = new SceneOverlayRenderer(
            Configuration, treasureTracker, occultEventTracker, gameGui, condition, objectTable, vnavmesh);
        mainWindow = new MainWindow(Configuration, treasureTracker, SaveConfiguration, OpenConfiguration,
            OpenCeWatch, OpenFateWatch, OpenMap);
        configWindow = new ConfigWindow(Configuration, SaveConfiguration,
            ApplyOverlayVisibility, ApplyMapDetailsVisibility, pluginInterface, navigationService);
        ceWatchWindow = new CeWatchWindow(
            Configuration, clientState, dataManager, occultEventTracker, navigationService, SaveConfiguration);
        fateWatchWindow = new FateWatchWindow(
            Configuration, clientState, dataManager, occultEventTracker, navigationService, SaveConfiguration);
        treasureMapWindow = new TreasureMapWindow(
            Configuration, treasureTracker, occultEventTracker, partyList, playerState, clientState,
            dataManager, textureProvider, navigationService, OpenCeWatch, OpenFateWatch, OpenConfiguration, SaveConfiguration,
            ToggleMapDetails);
        mapDetailsWindow = new MapDetailsWindow(Configuration, treasureTracker, occultEventTracker,
            navigationService, treasureSurveyService, treasureMapWindow, OpenCeWatch, OpenFateWatch, SaveConfiguration);
        treasureTracker.SupportedTerritoryChanged += OnSupportedTerritoryChanged;
        windows.AddWindow(mainWindow);
        windows.AddWindow(configWindow);
        windows.AddWindow(ceWatchWindow);
        windows.AddWindow(fateWatchWindow);
        windows.AddWindow(treasureMapWindow);
        windows.AddWindow(mapDetailsWindow);

        commandManager.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开新月罗盘；可用 config、ce、fate、reset、next、pause、resume"
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.Draw += sceneOverlayRenderer.Draw;
        pluginInterface.UiBuilder.Draw += watchedEventNotificationService.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMain;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfiguration;
        mainWindow.IsOpen = false;
        treasureMapWindow.IsOpen = Configuration.ShowOverlay && treasureTracker.IsSupportedTerritory;
        mapDetailsWindow.IsOpen = treasureMapWindow.IsOpen && Configuration.MapDetailsExpanded;
        log.Information("CrescentCompass 0.1.0 initialized without DailyRoutines dependencies.");
    }

    public PluginConfiguration Configuration { get; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Configuration.ShowMainWindow = mainWindow.IsOpen;
        SaveConfiguration();
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfiguration;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.Draw -= sceneOverlayRenderer.Draw;
        pluginInterface.UiBuilder.Draw -= watchedEventNotificationService.Draw;
        commandManager.RemoveHandler(MainCommand);
        windows.RemoveAllWindows();
        treasureTracker.SupportedTerritoryChanged -= OnSupportedTerritoryChanged;
        treasureSurveyService.Dispose();
        watchedEventNotificationService.Dispose();
        navigationService.Dispose();
        occultEventTracker.Dispose();
        treasureTracker.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "config":
                OpenConfiguration();
                break;
            case "pause":
                Configuration.Paused = true;
                SaveConfiguration();
                break;
            case "resume":
                Configuration.Paused = false;
                SaveConfiguration();
                break;
            case "reset":
                treasureTracker.Reset();
                break;
            case "next":
                treasureTracker.FocusNext();
                break;
            case "cancel":
                navigationService.Cancel();
                break;
            case "ce":
                ceWatchWindow.IsOpen = true;
                break;
            case "fate":
                fateWatchWindow.IsOpen = true;
                break;
            case "map":
                treasureMapWindow.IsOpen = true;
                break;
            default:
                Configuration.ShowOverlay = !treasureMapWindow.IsOpen;
                treasureMapWindow.IsOpen = Configuration.ShowOverlay && treasureTracker.IsSupportedTerritory;
                SaveConfiguration();
                break;
        }
    }

    private void OpenMain() => OpenMap();

    private void OpenConfiguration() => configWindow.IsOpen = true;

    private void OpenCeWatch() => ceWatchWindow.IsOpen = true;

    private void OpenFateWatch() => fateWatchWindow.IsOpen = true;

    private void OpenMap()
    {
        Configuration.ShowOverlay = true;
        treasureMapWindow.IsOpen = treasureTracker.IsSupportedTerritory;
        mapDetailsWindow.IsOpen = treasureMapWindow.IsOpen && Configuration.MapDetailsExpanded;
        SaveConfiguration();
    }

    private void ApplyOverlayVisibility()
    {
        treasureMapWindow.IsOpen = Configuration.ShowOverlay && treasureTracker.IsSupportedTerritory;
        mapDetailsWindow.IsOpen = treasureMapWindow.IsOpen && Configuration.MapDetailsExpanded;
    }

    private void ApplyMapDetailsVisibility() =>
        mapDetailsWindow.IsOpen = treasureMapWindow.IsOpen && Configuration.MapDetailsExpanded;

    private void OnSupportedTerritoryChanged(bool supported)
    {
        treasureMapWindow.IsOpen = supported && Configuration.ShowOverlay;
        mapDetailsWindow.IsOpen = supported && treasureMapWindow.IsOpen && Configuration.MapDetailsExpanded;
    }

    private void ToggleMapDetails()
    {
        Configuration.MapDetailsExpanded = !mapDetailsWindow.IsOpen;
        mapDetailsWindow.IsOpen = Configuration.MapDetailsExpanded && treasureMapWindow.IsOpen;
        SaveConfiguration();
    }

    private void SaveConfiguration()
    {
        Configuration.Normalize();
        pluginInterface.SavePluginConfig(Configuration);
    }

}
