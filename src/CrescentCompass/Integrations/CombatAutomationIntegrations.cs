using CrescentCompass.Configuration;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace CrescentCompass.Integrations;

public readonly record struct CombatDependencyStatus(
    string Name,
    bool Installed,
    bool Loaded,
    bool Controllable,
    string Detail);

public sealed class CombatAutomationIntegrations
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string> bossModGetAiPreset;
    private readonly ICallGateSubscriber<string, object> bossModSetAiPreset;
    private readonly ICallGateSubscriber<bool> aeAssistRunning;
    private readonly ICallGateSubscriber<bool, object> aeAssistSetRunning;
    private readonly ICallGateSubscriber<bool> aeAssistPullEnabled;
    private readonly ICallGateSubscriber<bool, object> aeAssistSetPullEnabled;
    private readonly ICallGateSubscriber<bool> promeStart;
    private readonly ICallGateSubscriber<bool> promeStop;
    private readonly ICallGateSubscriber<bool> promeRunning;
    private readonly ICallGateSubscriber<bool> rotationSolverActive;
    private bool armed;
    private bool bossModStarted;
    private bool rotationSolverStarted;
    private bool aeAssistRunningStarted;
    private bool aeAssistPullStarted;
    private bool promeStarted;
    private string previousBossModAiPreset = string.Empty;

    public CombatAutomationIntegrations(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        bossModGetAiPreset = pluginInterface.GetIpcSubscriber<string>("BossMod.AI.GetPreset");
        bossModSetAiPreset = pluginInterface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset");
        aeAssistRunning = pluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsRunning");
        aeAssistSetRunning =
            pluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetRunning");
        aeAssistPullEnabled =
            pluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsPullEnabled");
        aeAssistSetPullEnabled =
            pluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetPullEnabled");
        promeStart = pluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Start");
        promeStop = pluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Stop");
        promeRunning = pluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.IsRunning");
        rotationSolverActive =
            pluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
    }

    public bool IsArmed => armed;
    public string Status { get; private set; } = "战斗接管尚未启动";

    public static bool ManagesTargetSelection(
        EventMechanicProvider mechanicProvider,
        CombatRotationProvider rotationProvider) =>
        rotationProvider is CombatRotationProvider.AEAssistV3 or
            CombatRotationProvider.PromeRotation or
            CombatRotationProvider.RotationSolverReborn or
            CombatRotationProvider.BossModReborn ||
        mechanicProvider == EventMechanicProvider.BossModReborn;

    public static bool ManagesMovement(
        EventMechanicProvider mechanicProvider,
        CombatRotationProvider rotationProvider) =>
        mechanicProvider == EventMechanicProvider.BossModReborn ||
        rotationProvider == CombatRotationProvider.BossModReborn;

    public IReadOnlyList<CombatDependencyStatus> Dependencies =>
    [
        StatusFor("BossModReborn", "BossmodRebornCN", BossModReady(),
            BossModReady() ? "机制移动与自身循环可接管" : "需要 BossMod AI IPC"),
        StatusFor("AEAssistV3", "AEAssistV3", AEAssistReady(),
            AEAssistReady() ? "循环运行与主动攻击 IPC 可接管" : "需要 CombatRoutine IPC"),
        StatusFor("PromeRotation", "PromeRotation", PromeReady(),
            PromeReady() ? "循环与自动攻击 IPC 可接管" : "需要 PromeRotation IPC"),
        StatusFor("RotationSolverReborn", "Rotation Solver Reborn",
            rotationSolverActive.HasFunction, "支持自动模式启停与运行状态检测")
    ];

    public bool CanArm(
        EventMechanicProvider mechanicProvider,
        CombatRotationProvider rotationProvider,
        string bossModPreset,
        out string reason)
    {
        if ((mechanicProvider == EventMechanicProvider.BossModReborn ||
             rotationProvider == CombatRotationProvider.BossModReborn) &&
            !BossModReady())
        {
            reason = "BossmodRebornCN 未加载，或 AI IPC 尚未就绪。";
            return false;
        }

        switch (rotationProvider)
        {
            case CombatRotationProvider.AEAssistV3 when !AEAssistReady():
                reason = "AEAssistV3 未加载，或 CombatRoutine IPC 尚未就绪。";
                return false;
            case CombatRotationProvider.PromeRotation when !PromeReady():
                reason = "PromeRotation 未加载，或启停 IPC 尚未就绪。";
                return false;
            case CombatRotationProvider.RotationSolverReborn when
                !PluginLoaded("RotationSolverReborn") || !rotationSolverActive.HasFunction:
                reason = "Rotation Solver Reborn 未加载，或运行状态 IPC 尚未就绪。";
                return false;
            case CombatRotationProvider.BossModReborn when string.IsNullOrWhiteSpace(bossModPreset):
                reason = "选择 BossmodRebornCN 循环时需要填写 AI 预设名称。";
                return false;
            default:
                reason = string.Empty;
                return true;
        }
    }

    public bool Arm(
        EventMechanicProvider mechanicProvider,
        CombatRotationProvider rotationProvider,
        string bossModPreset,
        out string error)
    {
        if (armed)
        {
            error = string.Empty;
            return true;
        }
        if (!CanArm(mechanicProvider, rotationProvider, bossModPreset, out error))
        {
            Status = error;
            return false;
        }

        try
        {
            var useBossMod = mechanicProvider == EventMechanicProvider.BossModReborn ||
                             rotationProvider == CombatRotationProvider.BossModReborn;
            if (useBossMod)
            {
                previousBossModAiPreset = bossModGetAiPreset.InvokeFunc() ?? string.Empty;
                var requestedPreset = rotationProvider == CombatRotationProvider.BossModReborn
                    ? bossModPreset.Trim()
                    : string.Empty;
                bossModSetAiPreset.InvokeAction(requestedPreset);
                commandManager.ProcessCommand("/bmrai on");
                bossModStarted = true;
            }

            if (rotationProvider == CombatRotationProvider.RotationSolverReborn)
            {
                var wasActive = rotationSolverActive.HasFunction && rotationSolverActive.InvokeFunc();
                if (!wasActive)
                {
                    commandManager.ProcessCommand("/rotation Auto");
                    rotationSolverStarted = true;
                }
            }

            if (rotationProvider == CombatRotationProvider.PromeRotation)
            {
                var wasRunning = promeRunning.InvokeFunc();
                if (!wasRunning)
                {
                    commandManager.ProcessCommand("/pr autopull on");
                    if (!promeStart.InvokeFunc())
                    {
                        commandManager.ProcessCommand("/pr autopull off");
                        throw new InvalidOperationException("PromeRotation 拒绝启动自动循环。");
                    }
                    promeStarted = true;
                }
            }

            if (rotationProvider == CombatRotationProvider.AEAssistV3)
            {
                var wasRunning = aeAssistRunning.InvokeFunc();
                var wasPullEnabled = aeAssistPullEnabled.InvokeFunc();
                if (!wasRunning)
                {
                    aeAssistSetRunning.InvokeAction(true);
                    aeAssistRunningStarted = true;
                }
                if (!wasPullEnabled)
                {
                    aeAssistSetPullEnabled.InvokeAction(true);
                    aeAssistPullStarted = true;
                }
            }

            armed = true;
            Status = mechanicProvider == EventMechanicProvider.None &&
                     rotationProvider == CombatRotationProvider.None
                ? "未启用战斗插件，继续跟踪事件"
                : RotationLabel(rotationProvider) == "关闭"
                    ? "机制接管已启动"
                    : $"机制与 {RotationLabel(rotationProvider)} 循环已启动";
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Unable to arm event combat automation integrations.");
            Disarm();
            error = $"战斗插件接管失败：{exception.GetType().Name} · {exception.Message}";
            Status = error;
            return false;
        }
    }

    public void Disarm()
    {
        Exception? restoreFailure = null;
        void Restore(string operation, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                restoreFailure ??= exception;
                log.Warning(exception, "Unable to restore {Operation} after event automation.", operation);
            }
        }

        if (aeAssistPullStarted)
            Restore("AEAssist AutoPull", () =>
            {
                if (aeAssistPullEnabled.HasFunction && aeAssistPullEnabled.InvokeFunc())
                    aeAssistSetPullEnabled.InvokeAction(false);
            });
        if (aeAssistRunningStarted)
            Restore("AEAssist rotation", () =>
            {
                if (aeAssistRunning.HasFunction && aeAssistRunning.InvokeFunc())
                    aeAssistSetRunning.InvokeAction(false);
            });
        if (promeStarted)
            Restore("PromeRotation", () =>
            {
                if (promeStop.HasFunction) promeStop.InvokeFunc();
                commandManager.ProcessCommand("/pr autopull off");
            });
        if (rotationSolverStarted)
            Restore("Rotation Solver Reborn", () => commandManager.ProcessCommand("/rotation Off"));
        if (bossModStarted)
            Restore("BossmodRebornCN", () =>
            {
                commandManager.ProcessCommand("/bmrai off");
                if (bossModSetAiPreset.HasAction)
                    bossModSetAiPreset.InvokeAction(previousBossModAiPreset);
            });

        if (restoreFailure is not null)
        {
            Status = $"恢复战斗插件状态失败：{restoreFailure.GetType().Name} · {restoreFailure.Message}";
        }
        else
        {
            Status = "战斗接管已释放";
        }

        armed = false;
        bossModStarted = false;
        rotationSolverStarted = false;
        aeAssistRunningStarted = false;
        aeAssistPullStarted = false;
        promeStarted = false;
        previousBossModAiPreset = string.Empty;
    }

    private CombatDependencyStatus StatusFor(
        string internalName,
        string displayName,
        bool controllable,
        string readyDetail)
    {
        var plugin = FindPlugin(internalName);
        var installed = plugin.Installed;
        var loaded = plugin.Loaded;
        var detail = !installed ? "未安装" : !loaded ? "未启用" :
            controllable ? readyDetail : readyDetail;
        return new(displayName, installed, loaded, loaded && controllable, detail);
    }

    private bool BossModReady() =>
        PluginLoaded("BossModReborn") &&
        bossModGetAiPreset.HasFunction &&
        bossModSetAiPreset.HasAction;

    private bool AEAssistReady() =>
        PluginLoaded("AEAssistV3") &&
        aeAssistRunning.HasFunction &&
        aeAssistSetRunning.HasAction &&
        aeAssistPullEnabled.HasFunction &&
        aeAssistSetPullEnabled.HasAction;

    private bool PromeReady() =>
        PluginLoaded("PromeRotation") &&
        promeStart.HasFunction &&
        promeStop.HasFunction &&
        promeRunning.HasFunction;

    private bool PluginLoaded(string internalName) => FindPlugin(internalName).Loaded;

    private (bool Installed, bool Loaded) FindPlugin(string internalName)
    {
        var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(item =>
            string.Equals(item.InternalName, internalName, StringComparison.OrdinalIgnoreCase) ||
            item.InternalName.Contains(internalName, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Contains(internalName, StringComparison.OrdinalIgnoreCase));
        return (plugin != null, plugin?.IsLoaded == true);
    }

    private static string RotationLabel(CombatRotationProvider provider) => provider switch
    {
        CombatRotationProvider.AEAssistV3 => "AEAssistV3",
        CombatRotationProvider.PromeRotation => "PromeRotation",
        CombatRotationProvider.RotationSolverReborn => "Rotation Solver Reborn",
        CombatRotationProvider.BossModReborn => "BossmodRebornCN",
        _ => "关闭"
    };
}
