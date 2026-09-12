using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CrescentCompass.Services;

public sealed unsafe class TreasureSurveyService : IDisposable
{
    private const uint TreasureSightGeneralActionId = 32;
    private const byte FreelancerSupportJobId = 0;
    private static readonly Regex CountPattern = new(
        @"(?<silver>\d+)\s*个银宝箱.*?(?<bronze>\d+)\s*个铜宝箱",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private readonly Configuration.PluginConfiguration configuration;
    private readonly TreasureTracker tracker;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private SurveyStage stage;
    private byte originalSupportJob;
    private uint requestedTerritory;
    private long deadline;
    private long nextActionAt;
    private bool autoSurveyPending;
    private bool disposed;

    public TreasureSurveyService(
        Configuration.PluginConfiguration configuration,
        TreasureTracker tracker,
        IClientState clientState,
        ICondition condition,
        IFramework framework,
        IAddonLifecycle addonLifecycle,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.tracker = tracker;
        this.clientState = clientState;
        this.condition = condition;
        this.framework = framework;
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "_WideText", OnWideTextPostDraw);
        clientState.TerritoryChanged += OnTerritoryChanged;
        framework.Update += OnFrameworkUpdate;
        ResetForTerritory();
    }

    public int? BronzeCount { get; private set; }
    public int? SilverCount { get; private set; }
    public DateTimeOffset? LastSurveyAt { get; private set; }
    public string Status { get; private set; } = "尚未调查";
    public bool IsBusy => stage != SurveyStage.Idle;

    public bool RequestSurvey()
    {
        if (!tracker.IsSupportedTerritory)
        {
            Status = "只能在新月岛南部或北部调查";
            return false;
        }
        if (IsBusy) return false;
        if (!CanAct())
        {
            Status = "当前状态无法调查，脱战后重试";
            return false;
        }

        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            Status = "无法读取当前辅助职业";
            return false;
        }

        originalSupportJob = state->CurrentSupportJob;
        requestedTerritory = clientState.TerritoryType;
        deadline = Environment.TickCount64 + 15_000;
        nextActionAt = 0;
        stage = originalSupportJob == FreelancerSupportJobId
            ? SurveyStage.Casting
            : SurveyStage.SwitchingToFreelancer;
        Status = stage == SurveyStage.Casting ? "正在使用魔寻宝" : "正在切换至自由人";
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        addonLifecycle.UnregisterListener(AddonEvent.PostDraw, "_WideText", OnWideTextPostDraw);
    }

    private void OnTerritoryChanged(uint territoryId) => ResetForTerritory();

    private void ResetForTerritory()
    {
        BronzeCount = null;
        SilverCount = null;
        LastSurveyAt = null;
        stage = SurveyStage.Idle;
        autoSurveyPending = configuration.AutoSurveyFieldTreasureCounts && tracker.IsSupportedTerritory;
        Status = autoSurveyPending ? "等待自动调查" : "尚未调查";
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (autoSurveyPending && !IsBusy && tracker.IsSupportedTerritory && CanAct())
        {
            autoSurveyPending = false;
            RequestSurvey();
        }
        if (!IsBusy) return;

        var now = Environment.TickCount64;
        if (!tracker.IsSupportedTerritory || clientState.TerritoryType != requestedTerritory)
        {
            stage = SurveyStage.Idle;
            Status = "区域已变化，调查已取消";
            return;
        }
        if (now >= deadline)
        {
            if (stage == SurveyStage.Restoring)
            {
                stage = SurveyStage.Idle;
                Status = BronzeCount.HasValue ? "调查完成；原辅助职业恢复超时" : "调查与职业恢复超时";
                return;
            }
            Status = "调查超时";
            BeginRestore();
            return;
        }
        if (!CanAct()) return;

        var state = PublicContentOccultCrescent.GetState();
        if (state == null) return;
        switch (stage)
        {
            case SurveyStage.SwitchingToFreelancer:
                if (state->CurrentSupportJob == FreelancerSupportJobId)
                {
                    stage = SurveyStage.Casting;
                    nextActionAt = now + 250;
                    Status = "正在使用魔寻宝";
                }
                else if (now >= nextActionAt)
                {
                    PublicContentOccultCrescent.ChangeSupportJob(FreelancerSupportJobId);
                    nextActionAt = now + 750;
                }
                break;
            case SurveyStage.Casting:
                if (now < nextActionAt) break;
                var actionManager = ActionManager.Instance();
                if (actionManager == null) break;
                var remaining = actionManager->GetRecastTime(ActionType.GeneralAction, TreasureSightGeneralActionId) -
                                actionManager->GetRecastTimeElapsed(ActionType.GeneralAction, TreasureSightGeneralActionId);
                if (remaining > 0.05f)
                {
                    Status = $"魔寻宝冷却中 · {MathF.Ceiling(remaining):F0}秒";
                    nextActionAt = now + 250;
                    break;
                }
                if (!actionManager->UseAction(ActionType.GeneralAction, TreasureSightGeneralActionId))
                {
                    Status = "魔寻宝不可用，请确认自由人达到10级";
                    BeginRestore();
                    break;
                }
                stage = SurveyStage.WaitingForResult;
                deadline = now + 5_000;
                Status = "正在读取宝箱数量";
                break;
            case SurveyStage.Restoring:
                if (state->CurrentSupportJob == originalSupportJob)
                {
                    stage = SurveyStage.Idle;
                    Status = BronzeCount.HasValue ? "调查完成" : Status;
                }
                else if (state->CurrentSupportJob != FreelancerSupportJobId)
                {
                    stage = SurveyStage.Idle;
                }
                else if (now >= nextActionAt)
                {
                    PublicContentOccultCrescent.ChangeSupportJob(originalSupportJob);
                    nextActionAt = now + 750;
                }
                break;
        }
    }

    private void OnWideTextPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!tracker.IsSupportedTerritory || args.Addon.Address == nint.Zero) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible) return;
        var resourceNode = addon->GetNodeById(3);
        if (resourceNode == null) return;
        var node = resourceNode->GetAsAtkTextNode();
        if (node == null) return;
        var match = CountPattern.Match(node->NodeText.ToString());
        if (!match.Success || !int.TryParse(match.Groups["silver"].Value, out var silver) ||
            !int.TryParse(match.Groups["bronze"].Value, out var bronze))
            return;

        SilverCount = silver;
        BronzeCount = bronze;
        LastSurveyAt = DateTimeOffset.Now;
        Status = "调查完成";
        log.Information("Treasure survey found {Silver} silver and {Bronze} bronze coffers in territory {Territory}.",
            silver, bronze, clientState.TerritoryType);
        if (IsBusy) BeginRestore();
    }

    private bool CanAct() =>
        !condition[ConditionFlag.InCombat] &&
        !condition[ConditionFlag.Casting] &&
        !condition[ConditionFlag.BetweenAreas] &&
        !condition[ConditionFlag.BetweenAreas51];

    private void BeginRestore()
    {
        if (originalSupportJob == FreelancerSupportJobId)
        {
            stage = SurveyStage.Idle;
            return;
        }
        stage = SurveyStage.Restoring;
        deadline = Environment.TickCount64 + 8_000;
        nextActionAt = 0;
    }

    private enum SurveyStage
    {
        Idle,
        SwitchingToFreelancer,
        Casting,
        WaitingForResult,
        Restoring
    }
}
