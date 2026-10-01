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
    private const string CrescentBossModPresetName = "CrescentCompass - Target & Mechanics";
    private const string CrescentBossModPresetJson =
        "{\"Name\":\"CrescentCompass - Target & Mechanics\",\"Modules\":{\"BossMod.Autorotation.MiscAI.FollowSlot\":[]}}";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string> bossModGetAiPreset;
    private readonly ICallGateSubscriber<string, object> bossModSetAiPreset;
    private readonly ICallGateSubscriber<string, bool, bool> bossModCreatePreset;
    private readonly ICallGateSubscriber<uint, bool> bossModHasModuleByDataId;
    private readonly ICallGateSubscriber<bool> bossModHasActiveModule;
    private readonly ICallGateSubscriber<string> bossModActiveModuleName;
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
    private bool bossModPresetChanged;
    private bool bossModTargetingReady;
    private bool bossModFollowConfigured;
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
        bossModCreatePreset =
            pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        bossModHasModuleByDataId = pluginInterface.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId");
        bossModHasActiveModule = pluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
        bossModActiveModuleName = pluginInterface.GetIpcSubscriber<string>("BossMod.ActiveModuleName");
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

    public bool ShouldBossModControlEncounter(
        EventMechanicProvider mechanicProvider,
        CombatRotationProvider rotationProvider,
        IEnumerable<uint> enemyDataIds)
    {
        if (!armed || !bossModTargetingReady || mechanicProvider != EventMechanicProvider.BossModReborn &&
            rotationProvider != CombatRotationProvider.BossModReborn)
            return false;
        return HasBossModEncounterModule(enemyDataIds);
    }

    public bool CanStartBossModEncounterHandoff(
        EventMechanicProvider mechanicProvider,
        CombatRotationProvider rotationProvider,
        IEnumerable<uint> enemyDataIds)
    {
        if (mechanicProvider != EventMechanicProvider.BossModReborn &&
            rotationProvider != CombatRotationProvider.BossModReborn)
            return false;
        if (!BossModMechanicsReady()) return false;
        return HasBossModEncounterModule(enemyDataIds);
    }

    private bool HasBossModEncounterModule(IEnumerable<uint> enemyDataIds)
    {
        try
        {
            if (bossModHasActiveModule.HasFunction && bossModHasActiveModule.InvokeFunc()) return true;
            return bossModHasModuleByDataId.HasFunction &&
                   enemyDataIds.Where(id => id != 0).Distinct().Any(id => bossModHasModuleByDataId.InvokeFunc(id));
        }
        catch (Exception exception)
        {
            log.Debug(exception, "Unable to query BossMod encounter-module support.");
            return false;
        }
    }

    public string BossModModuleLabel()
    {
        try
        {
            var name = bossModActiveModuleName.HasFunction ? bossModActiveModuleName.InvokeFunc() : string.Empty;
            return string.IsNullOrWhiteSpace(name) ? "BossmodRebornCN 机制模块" : name;
        }
        catch
        {
            return "BossmodRebornCN 机制模块";
        }
    }

    public IReadOnlyList<CombatDependencyStatus> Dependencies =>
    [
        StatusFor("BossModReborn", "BossmodRebornCN", BossModMechanicsReady(),
            BossModMechanicsReady() ? "使用 BossMod 默认配置接管目标与机制移动" : "需要 BossMod 模块 IPC"),
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
        if (rotationProvider == CombatRotationProvider.BossModReborn && !BossModReady())
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
        bool enableBossModMechanics,
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
            var useBossMod = rotationProvider == CombatRotationProvider.BossModReborn ||
                             mechanicProvider == EventMechanicProvider.BossModReborn && enableBossModMechanics;
            if (useBossMod)
            {
                if (rotationProvider == CombatRotationProvider.BossModReborn)
                {
                    previousBossModAiPreset = bossModGetAiPreset.InvokeFunc() ?? string.Empty;
                    bossModSetAiPreset.InvokeAction(bossModPreset.Trim());
                    var appliedPreset = bossModGetAiPreset.InvokeFunc() ?? string.Empty;
                    if (!string.Equals(appliedPreset.Trim(), bossModPreset.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"BossmodRebornCN 中未找到 AI 预设“{bossModPreset.Trim()}”。");
                    bossModPresetChanged = true;
                }
                else
                {
                    previousBossModAiPreset = bossModGetAiPreset.InvokeFunc() ?? string.Empty;
                    if (!bossModCreatePreset.HasFunction ||
                        !bossModCreatePreset.InvokeFunc(CrescentBossModPresetJson, true))
                        throw new InvalidOperationException("BossmodRebornCN 无法建立新月罗盘追击预设。");
                    bossModSetAiPreset.InvokeAction(CrescentBossModPresetName);
                    var appliedPreset = bossModGetAiPreset.InvokeFunc() ?? string.Empty;
                    if (!string.Equals(appliedPreset, CrescentBossModPresetName,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("BossmodRebornCN 未能启用新月罗盘追击预设。");
                    bossModPresetChanged = true;
                }
                ConfigureBossModFollow();
                commandManager.ProcessCommand("/bmrai on");
                bossModStarted = true;
                bossModTargetingReady = true;
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
            Status = mechanicProvider == EventMechanicProvider.BossModReborn && !bossModTargetingReady &&
                     rotationProvider != CombatRotationProvider.BossModReborn
                ? $"BossMod 当前事件未接管，使用新月罗盘索敌 · {RotationLabel(rotationProvider)} 循环已启动"
                : mechanicProvider == EventMechanicProvider.None &&
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

    private void ConfigureBossModFollow()
    {
        if (bossModFollowConfigured) return;
        commandManager.ProcessCommand("/bmrai forbidmovement off");
        commandManager.ProcessCommand("/bmrai followcombat on");
        commandManager.ProcessCommand("/bmrai followmodule on");
        commandManager.ProcessCommand("/bmrai followtarget on");
        commandManager.ProcessCommand("/bmrai followoutofcombat off");
        commandManager.ProcessCommand("/bmrai idlewhilemounted off");
        bossModFollowConfigured = true;
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
            Restore("BossmodRebornCN AI", () => commandManager.ProcessCommand("/bmrai off"));
        if (bossModPresetChanged)
            Restore("BossmodRebornCN preset", () => bossModSetAiPreset.InvokeAction(previousBossModAiPreset));

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
        bossModPresetChanged = false;
        bossModTargetingReady = false;
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
        BossModMechanicsReady() &&
        bossModGetAiPreset.HasFunction &&
        bossModSetAiPreset.HasAction;

    private bool BossModMechanicsReady() =>
        PluginLoaded("BossModReborn") &&
        (bossModHasActiveModule.HasFunction || bossModHasModuleByDataId.HasFunction);

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
