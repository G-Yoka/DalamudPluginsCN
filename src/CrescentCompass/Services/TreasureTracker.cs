using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace CrescentCompass.Services;

public sealed unsafe class TreasureTracker : IDisposable
{
    private const long TreasureSpawnCaptureWindowMilliseconds = 8_000;
    private readonly PluginConfiguration configuration;
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly Action saveConfiguration;
    private readonly PotPredictionSession session = new();
    private PotCandidateCatalogResult catalog = new([], [], string.Empty, string.Empty);
    private bool disposed;
    private bool wasPlayerDead;
    private Vector3? treasureReportPosition;
    private long treasureConfirmationDeadline;
    private long terminalResetAt;
    private long nextTreasureScan;
    private long nextFieldTreasureScan;
    private long nextFieldTreasureLayoutLoad;
    private readonly List<FieldTreasureSnapshot> fieldTreasures = [];
    private IReadOnlyList<FieldTreasurePoint> fieldTreasurePoints = [];
    private ulong calibratedTreasureObjectId;
    private uint calibrationCandidateId;

    public TreasureTracker(
        PluginConfiguration configuration,
        IChatGui chatGui,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IDataManager dataManager,
        IPluginLog log,
        Action saveConfiguration)
    {
        this.configuration = configuration;
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.dataManager = dataManager;
        this.log = log;
        this.saveConfiguration = saveConfiguration;
        chatGui.ChatMessage += OnChatMessage;
        clientState.TerritoryChanged += OnTerritoryChanged;
        framework.Update += OnFrameworkUpdate;
        EnterTerritory();
    }

    public PotPredictionSession Session => session;
    public event Action<bool>? SupportedTerritoryChanged;
    public bool IsSupportedTerritory => clientState.TerritoryType is
        PotCandidateCatalog.NorthHornTerritoryId or PotCandidateCatalog.SouthHornTerritoryId;
    public uint TerritoryId => clientState.TerritoryType;
    public uint MapId => clientState.MapId;
    public bool IsTreasureHuntActive => !configuration.Paused &&
        session.Stage is PotSessionStage.Predicting or PotSessionStage.AwaitingTreasure;
    public string AreaName => clientState.TerritoryType == PotCandidateCatalog.NorthHornTerritoryId
        ? "新月岛北部"
        : clientState.TerritoryType == PotCandidateCatalog.SouthHornTerritoryId ? "新月岛南部" : "区域外";
    public string Status { get; private set; } = "等待进入新月岛北部或南部";
    public PotCandidate? FocusedCandidate { get; private set; }
    public Vector3? PlayerPosition => objectTable.LocalPlayer?.Position;
    public float? PlayerRotation => objectTable.LocalPlayer?.Rotation;
    public float? CameraRotation
    {
        get
        {
            var cameraManager = CameraManager.Instance();
            if (cameraManager == null || cameraManager->Camera == null) return null;
            var rotation = (cameraManager->Camera->DirH + MathF.PI) % MathF.Tau;
            return rotation < 0f ? rotation + MathF.Tau : rotation;
        }
    }
    public TreasureSnapshot? ConfirmedTreasure { get; private set; }
    public IReadOnlyList<FieldTreasureSnapshot> FieldTreasures => fieldTreasures;
    public IReadOnlyList<FieldTreasurePoint> FieldTreasurePoints => fieldTreasurePoints;
    public IReadOnlyList<PotHint> VisibleHints => session.ConflictingHint is { } conflict
        ? [.. session.AcceptedHints, conflict]
        : session.AcceptedHints;
    public IReadOnlyList<PotCandidate> VisibleCandidates => session.AcceptedHints.Count > 0 ? session.Candidates : [];
    public string PredictionCandidateName => session.Round >= 2 ? "第二次机会宝藏候选" : "魔法罐宝藏候选";
    public string ActualTreasureName => session.Round >= 2 ? "第二次机会宝藏" : "魔法罐宝藏";
    public int CandidateCalibrationCount => configuration.PotCandidateCalibrations.Count;
    public string CandidateCalibrationStatus { get; private set; } = "尚未捕获宝箱实际位置";

    public bool IsFieldTreasureObject(IGameObject gameObject) =>
        gameObject.ObjectKind == ObjectKind.Treasure && IsFieldTreasurePosition(gameObject.Position);

    public string ExportCandidateCalibrations()
    {
        var lines = new List<string>
        {
            "TerritoryId\tCandidateId\tOriginalX\tOriginalZ\tCalibratedX\tCalibratedY\tCalibratedZ\tOffset\tSamples"
        };
        lines.AddRange(configuration.PotCandidateCalibrations
            .OrderBy(item => item.TerritoryId)
            .ThenBy(item => item.CandidateId)
            .Select(item =>
            {
                var source = PotCandidateCatalog.Read(item.TerritoryId).InitialCandidates
                    .Concat(PotCandidateCatalog.Read(item.TerritoryId).SecondChanceCandidates)
                    .FirstOrDefault(candidate => candidate.Id == item.CandidateId);
                var offset = source.Id == 0
                    ? float.NaN
                    : HorizontalDistance(source.Position, new Vector3(item.X, item.Y, item.Z));
                return FormattableString.Invariant($"{item.TerritoryId}\t{item.CandidateId}\t{source.Position.X:F4}\t{source.Position.Z:F4}\t{item.X:F4}\t{item.Y:F4}\t{item.Z:F4}\t{offset:F2}\t{item.SampleCount}");
            }));
        return string.Join(Environment.NewLine, lines);
    }

    public void ResetCandidateCalibrations()
    {
        configuration.PotCandidateCalibrations.Clear();
        saveConfiguration();
        RefreshCandidateUniverse();
        CandidateCalibrationStatus = "校准记录已清除";
        Status = "已清除魔法罐候选点校准记录";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        framework.Update -= OnFrameworkUpdate;
        chatGui.ChatMessage -= OnChatMessage;
        session.SetUniverse([]);
        fieldTreasures.Clear();
        fieldTreasurePoints = [];
    }

    public void Reset(string message = "已清空线索，等待罐子提示")
    {
        session.Reset();
        session.SetUniverse(ApplyCandidateCalibrations(catalog.InitialCandidates));
        FocusedCandidate = null;
        ConfirmedTreasure = null;
        treasureReportPosition = null;
        treasureConfirmationDeadline = 0;
        terminalResetAt = 0;
        calibratedTreasureObjectId = 0;
        calibrationCandidateId = 0;
        Status = IsSupportedTerritory ? message : "等待进入新月岛北部或南部";
    }

    public bool Undo()
    {
        if (!session.UndoLast()) return false;
        EnsureFocus();
        Status = session.AcceptedHints.Count == 0
            ? "已撤销全部线索，等待罐子提示"
            : $"已撤销最近提示，剩余 {session.Candidates.Count} 个候选";
        return true;
    }

    public bool FocusNext()
    {
        if (session.Candidates.Count == 0) return false;
        var current = FocusedCandidate is { } focused
            ? session.Candidates.ToList().FindIndex(candidate => candidate.Id == focused.Id)
            : -1;
        FocusedCandidate = session.Candidates[(current + 1) % session.Candidates.Count];
        return true;
    }

    public bool Focus(uint candidateId)
    {
        var candidate = session.Candidates.FirstOrDefault(item => item.Id == candidateId);
        if (candidate.Id == 0) return false;
        FocusedCandidate = candidate;
        return true;
    }

    public int GetCandidateNumber(PotCandidate candidate)
    {
        for (var index = 0; index < session.Universe.Count; index++)
            if (session.Universe[index].Id == candidate.Id)
                return index + 1;
        return 0;
    }

    private void OnTerritoryChanged(uint territoryId) => EnterTerritory();

    private void EnterTerritory()
    {
        session.Reset();
        fieldTreasures.Clear();
        fieldTreasurePoints = [];
        nextFieldTreasureScan = 0;
        nextFieldTreasureLayoutLoad = 0;
        FocusedCandidate = null;
        ConfirmedTreasure = null;
        treasureReportPosition = null;
        treasureConfirmationDeadline = 0;
        terminalResetAt = 0;
        calibratedTreasureObjectId = 0;
        calibrationCandidateId = 0;
        if (!IsSupportedTerritory)
        {
            catalog = new([], [], string.Empty, string.Empty);
            session.SetUniverse([]);
            Status = "等待进入新月岛北部或南部";
            SupportedTerritoryChanged?.Invoke(false);
            return;
        }

        var fallbackY = objectTable.LocalPlayer?.Position.Y ?? 0f;
        catalog = PotCandidateCatalog.Read(clientState.TerritoryType, fallbackY);
        ReloadFieldTreasurePoints();
        RestoreConfirmedFieldTreasures();
        session.SetUniverse(ApplyCandidateCalibrations(catalog.InitialCandidates));
        Status = catalog.IsAvailable
            ? $"等待罐子提示 · 已载入 {catalog.InitialCandidates.Count} 个候选点"
            : "等待罐子提示 · 候选库不可用";
        log.Debug("Loaded {Count} initial and {SecondCount} second-chance candidates for territory {Territory}.",
            catalog.InitialCandidates.Count, catalog.SecondChanceCandidates.Count, clientState.TerritoryType);
        SupportedTerritoryChanged?.Invoke(true);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (configuration.Paused || !IsSupportedTerritory || message.LogKind != XivChatType.SystemMessage) return;
        var text = message.OriginalMessage.ToString().Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (PotPredictionSession.IsSecondTreasureMessage(text))
        {
            if (session.Round >= 2) return;
            session.StartNextRound();
            session.SetUniverse(ApplyCandidateCalibrations(catalog.SecondChanceCandidates));
            FocusedCandidate = null;
            ConfirmedTreasure = null;
            treasureReportPosition = null;
            treasureConfirmationDeadline = 0;
            terminalResetAt = 0;
            calibratedTreasureObjectId = 0;
            calibrationCandidateId = 0;
            Status = "第 2 处财宝：等待罐子提示";
            return;
        }

        if (PotPredictionSession.IsTerminalMessage(text))
        {
            if (session.Stage == PotSessionStage.AwaitingTreasure && treasureReportPosition != null)
            {
                ScanNearbyTreasures();
                terminalResetAt = Environment.TickCount64 + TreasureSpawnCaptureWindowMilliseconds;
                Status = "本轮已结束，正在等待并捕获新出现的宝箱";
                return;
            }
            Reset("本轮已结束，已清空线索；等待下一次罐子提示");
            return;
        }

        if (PotPredictionSession.IsTreasureReportedMessage(text))
        {
            session.MarkTreasureReported();
            treasureReportPosition = objectTable.LocalPlayer?.Position ?? FocusedCandidate?.Position;
            treasureConfirmationDeadline = Environment.TickCount64 + 5_000;
            terminalResetAt = 0;
            nextTreasureScan = 0;
            ConfirmedTreasure = null;
            calibratedTreasureObjectId = 0;
            calibrationCandidateId = FocusedCandidate?.Id ?? 0;
            Status = "已收到发现提示，正在关联本轮宝箱";
            if (configuration.AutoCalibratePotCandidates)
                CandidateCalibrationStatus = "已收到发现提示，正在捕获宝箱实际位置";
            return;
        }

        if (objectTable.LocalPlayer?.Position is not Vector3 playerPosition || !PotPredictionSession.IsFinite(playerPosition))
            return;
        var result = session.TryApply(text, playerPosition, Environment.TickCount64);
        if (result is not PotHintApplyResult.NotHint and not PotHintApplyResult.Duplicate)
            log.Debug("Pot hint result {Result}; {Count} candidates remain.", result, session.Candidates.Count);

        switch (result)
        {
            case PotHintApplyResult.Accepted:
                EnsureFocus();
                Status = session.Candidates.Count switch
                {
                    0 => "提示已记录；候选库当前不可用",
                    1 => "已收敛为唯一预测点",
                    _ => $"提示已记录，剩余 {session.Candidates.Count} 个候选"
                };
                break;
            case PotHintApplyResult.Conflict:
                Status = "新提示与已有候选冲突，已保留上一组结果";
                break;
            case PotHintApplyResult.NoKnownCandidate:
                FocusedCandidate = null;
                Status = "提示未匹配已知点位；可撤销最近提示";
                break;
        }
    }

    private void EnsureFocus()
    {
        if (FocusedCandidate is { } current)
        {
            var refreshed = session.Candidates.FirstOrDefault(candidate => candidate.Id == current.Id);
            if (refreshed.Id != 0)
            {
                FocusedCandidate = refreshed;
                return;
            }
        }
        var player = objectTable.LocalPlayer?.Position;
        FocusedCandidate = player is { } position
            ? session.Candidates.OrderBy(candidate => HorizontalDistanceSquared(candidate.Position, position)).FirstOrDefault()
            : session.Candidates.FirstOrDefault();
        if (FocusedCandidate?.Id == 0) FocusedCandidate = null;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var playerDead = objectTable.LocalPlayer?.IsDead == true;
        if (playerDead && !wasPlayerDead &&
            (session.Stage != PotSessionStage.Waiting || session.AcceptedHints.Count > 0 ||
             FocusedCandidate != null || ConfirmedTreasure != null))
            Reset("玩家死亡，魔法罐寻宝候选已清空");
        wasPlayerDead = playerDead;

        var now = Environment.TickCount64;
        if (IsSupportedTerritory && fieldTreasurePoints.Count == 0 && now >= nextFieldTreasureLayoutLoad)
        {
            nextFieldTreasureLayoutLoad = now + 2_000;
            ReloadFieldTreasurePoints();
        }
        if (IsSupportedTerritory && now >= nextFieldTreasureScan)
        {
            nextFieldTreasureScan = now + 100;
            ScanFieldTreasures();
        }
        if (session.Stage != PotSessionStage.AwaitingTreasure || treasureReportPosition == null) return;
        if (now >= nextTreasureScan)
        {
            nextTreasureScan = now + 100;
            ScanNearbyTreasures();
        }

        if (terminalResetAt != 0 && now >= terminalResetAt)
        {
            Reset("本轮已结束，已清空线索；等待下一次罐子提示");
            return;
        }

        if (now < treasureConfirmationDeadline) return;
        Status = ConfirmedTreasure != null
            ? $"已确认{ActualTreasureLabel}"
            : $"发现提示已收到，{ActualTreasureLabel}对象尚未确认";
        if (ConfirmedTreasure == null && configuration.AutoCalibratePotCandidates)
            CandidateCalibrationStatus = "最近一次未校准：发现提示后未捕获到宝箱对象";
        treasureConfirmationDeadline = long.MaxValue;
    }

    private void ReloadFieldTreasurePoints()
    {
        fieldTreasurePoints = FieldTreasureLayoutReader.Read(dataManager, log, clientState.TerritoryType);
        RemoveFieldTreasureContaminatedCalibrations();
        if (fieldTreasurePoints.Count > 0)
            log.Information("Loaded {Count} field treasure points for territory {Territory}.",
                fieldTreasurePoints.Count, clientState.TerritoryType);
    }

    private void ScanNearbyTreasures()
    {
        if (treasureReportPosition is not { } reportPosition) return;
        TreasureSnapshot? nearest = null;
        var nearestOpened = false;
        var nearestDistance = float.MaxValue;
        foreach (var gameObject in objectTable)
        {
            if (gameObject == null || !gameObject.IsValid() ||
                gameObject.ObjectKind != ObjectKind.Treasure || gameObject.Address == nint.Zero)
                continue;
            if (IsFieldTreasureObject(gameObject)) continue;

            var treasure = (NativeTreasure*)(void*)gameObject.Address;
            if (treasure == null) continue;
            var opened = (treasure->Flags & NativeTreasure.TreasureFlags.Opened) != 0;
            if ((treasure->Flags & NativeTreasure.TreasureFlags.FadedOut) != 0 && !opened) continue;
            if (!gameObject.IsTargetable && !opened) continue;

            var distance = HorizontalDistance(reportPosition, gameObject.Position);
            if (distance > 35f || distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = new(gameObject.GameObjectId, gameObject.Position);
            nearestOpened = opened;
        }

        var previouslyConfirmed = ConfirmedTreasure != null;
        ConfirmedTreasure = nearest;
        if (nearest != null)
        {
            if (nearestOpened)
                RecordOpenedMagicPotTreasure(nearest.Value);
            Status = $"已确认{ActualTreasureLabel}";
        }
        else if (previouslyConfirmed)
        {
            Status = $"{ActualTreasureLabel}对象已消失或离开加载范围";
        }
    }

    public void RecordOpenedMagicPotTreasure(TreasureSnapshot treasure, uint candidateId = 0)
    {
        if (!configuration.AutoCalibratePotCandidates || calibratedTreasureObjectId == treasure.GameObjectId)
            return;
        if (IsFieldTreasurePosition(treasure.Position))
        {
            CandidateCalibrationStatus = "已忽略普通野外宝箱，未修改魔法罐候选点";
            return;
        }

        var catalogCandidates = session.Round >= 2 ? catalog.SecondChanceCandidates : catalog.InitialCandidates;
        var resolvedCandidateId = candidateId != 0
            ? candidateId
            : calibrationCandidateId != 0
                ? calibrationCandidateId
                : FocusedCandidate?.Id ?? (session.Candidates.Count == 1 ? session.Candidates[0].Id : 0);
        var candidate = catalogCandidates.FirstOrDefault(item => item.Id == resolvedCandidateId);
        if (candidate.Id == 0)
        {
            CandidateCalibrationStatus = "最近一次未校准：无法确定本轮使用的候选点";
            log.Debug("No selected pot candidate is associated with opened treasure {ObjectId}.", treasure.GameObjectId);
            return;
        }

        calibratedTreasureObjectId = treasure.GameObjectId;
        calibrationCandidateId = candidate.Id;
        var distance = HorizontalDistance(candidate.Position, treasure.Position);
        var record = configuration.PotCandidateCalibrations.FirstOrDefault(item =>
            item.TerritoryId == clientState.TerritoryType && item.CandidateId == candidate.Id);
        if (record == null)
        {
            record = new PotCandidateCalibrationRecord
            {
                TerritoryId = clientState.TerritoryType,
                CandidateId = candidate.Id,
                X = treasure.Position.X,
                Y = treasure.Position.Y,
                Z = treasure.Position.Z,
                SampleCount = 1
            };
            configuration.PotCandidateCalibrations.Add(record);
        }
        else
        {
            record.X = treasure.Position.X;
            record.Y = treasure.Position.Y;
            record.Z = treasure.Position.Z;
            record.SampleCount = Math.Min(20, Math.Max(1, record.SampleCount) + 1);
        }

        var calibratedPosition = new Vector3(record.X, record.Y, record.Z);
        session.UpdateCandidatePosition(candidate.Id, calibratedPosition);
        if (FocusedCandidate?.Id == candidate.Id)
            FocusedCandidate = new PotCandidate(candidate.Id, calibratedPosition);
        saveConfiguration();
        CandidateCalibrationStatus =
            $"已在开箱时覆盖候选 #{GetCandidateNumber(candidate):D2} 的真实坐标 · 原始偏差 {distance:F1} 米";
        log.Information(
            "Calibrated pot candidate {CandidateId} in territory {Territory}: offset {Offset:F1}m, samples {Samples}.",
            candidate.Id, clientState.TerritoryType, distance, record.SampleCount);
    }

    private IEnumerable<PotCandidate> ApplyCandidateCalibrations(IEnumerable<PotCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var record = configuration.PotCandidateCalibrations.FirstOrDefault(item =>
                item.TerritoryId == clientState.TerritoryType && item.CandidateId == candidate.Id);
            yield return record == null
                ? candidate
                : candidate with { Position = new Vector3(record.X, record.Y, record.Z) };
        }
    }

    private bool IsFieldTreasurePosition(Vector3 position) =>
        fieldTreasurePoints.Any(point => HorizontalDistanceSquared(point.Position, position) <= 3f * 3f) ||
        configuration.ConfirmedFieldTreasures.Any(item =>
            item.TerritoryId == clientState.TerritoryType &&
            HorizontalDistanceSquared(new Vector3(item.X, item.Y, item.Z), position) <= 3f * 3f);

    private void RemoveFieldTreasureContaminatedCalibrations()
    {
        if (fieldTreasurePoints.Count == 0) return;
        var removed = configuration.PotCandidateCalibrations.RemoveAll(item =>
            item.TerritoryId == clientState.TerritoryType &&
            fieldTreasurePoints.Any(point => HorizontalDistanceSquared(
                point.Position, new Vector3(item.X, item.Y, item.Z)) <= 3f * 3f));
        if (removed == 0) return;
        saveConfiguration();
        RefreshCandidateUniverse();
        CandidateCalibrationStatus = $"已移除 {removed} 条误用普通宝箱位置的候选点校准";
    }

    private void RefreshCandidateUniverse()
    {
        var source = session.Round >= 2 ? catalog.SecondChanceCandidates : catalog.InitialCandidates;
        session.SetUniverse(ApplyCandidateCalibrations(source));
        EnsureFocus();
    }

    private void ScanFieldTreasures()
    {
        var configurationChanged = false;
        foreach (var gameObject in objectTable)
        {
            if (gameObject == null || !gameObject.IsValid() ||
                gameObject.ObjectKind != ObjectKind.Treasure || gameObject.Address == nint.Zero)
                continue;

            var treasure = (NativeTreasure*)(void*)gameObject.Address;
            if (treasure == null)
                continue;

            if ((treasure->Flags & NativeTreasure.TreasureFlags.Opened) != 0)
            {
                configurationChanged |= RemoveConfirmedFieldTreasure(gameObject.GameObjectId, gameObject.Position);
                continue;
            }
            if (!gameObject.IsTargetable ||
                (treasure->Flags & NativeTreasure.TreasureFlags.FadedOut) != 0)
                continue;

            if (ConfirmedTreasure?.GameObjectId == gameObject.GameObjectId)
            {
                continue;
            }
            var layoutPoint = fieldTreasurePoints.FirstOrDefault(point =>
                HorizontalDistanceSquared(point.Position, gameObject.Position) <= 4f);
            if (layoutPoint.Id == 0) continue;
            var name = gameObject.Name.ToString();
            var kind = ClassifyFieldTreasure(gameObject.BaseId, name);
            if (kind == FieldTreasureKind.Unknown) kind = layoutPoint.Kind;
            var snapshot = new FieldTreasureSnapshot(gameObject.GameObjectId, gameObject.Position, kind);
            var existing = fieldTreasures.FindIndex(item => item.GameObjectId == gameObject.GameObjectId ||
                HorizontalDistanceSquared(item.Position, gameObject.Position) <= 1f);
            if (existing >= 0)
            {
                fieldTreasures[existing] = snapshot;
                var saved = configuration.ConfirmedFieldTreasures.FirstOrDefault(item =>
                    item.TerritoryId == clientState.TerritoryType &&
                    HorizontalDistanceSquared(new Vector3(item.X, item.Y, item.Z), snapshot.Position) <= 1f);
                if (saved != null && saved.Kind != (int)snapshot.Kind)
                {
                    saved.Kind = (int)snapshot.Kind;
                    configurationChanged = true;
                }
            }
            else
            {
                fieldTreasures.Add(snapshot);
                configuration.ConfirmedFieldTreasures.Add(new ConfirmedFieldTreasureRecord
                {
                    TerritoryId = clientState.TerritoryType,
                    X = snapshot.Position.X,
                    Y = snapshot.Position.Y,
                    Z = snapshot.Position.Z,
                    Kind = (int)snapshot.Kind
                });
                configurationChanged = true;
            }
        }
        if (configurationChanged) saveConfiguration();
    }

    private bool RemoveConfirmedFieldTreasure(ulong gameObjectId, Vector3 position)
    {
        fieldTreasures.RemoveAll(item => item.GameObjectId == gameObjectId ||
            HorizontalDistanceSquared(item.Position, position) <= 1f);
        return configuration.ConfirmedFieldTreasures.RemoveAll(item =>
            item.TerritoryId == clientState.TerritoryType &&
            HorizontalDistanceSquared(new Vector3(item.X, item.Y, item.Z), position) <= 1f) > 0;
    }

    private void RestoreConfirmedFieldTreasures()
    {
        foreach (var item in configuration.ConfirmedFieldTreasures)
        {
            if (item.TerritoryId != clientState.TerritoryType) continue;
            var kind = Enum.IsDefined(typeof(FieldTreasureKind), item.Kind)
                ? (FieldTreasureKind)item.Kind
                : FieldTreasureKind.Unknown;
            fieldTreasures.Add(new FieldTreasureSnapshot(0, new Vector3(item.X, item.Y, item.Z), kind));
        }
    }

    private FieldTreasureKind ClassifyFieldTreasure(uint baseId, string name)
    {
        const uint bronzeSgb = 1596;
        const uint silverSgb = 1597;
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Treasure>();
        var sgb = sheet.GetRowOrDefault(baseId)?.SGB.RowId ?? 0;
        if (sgb == silverSgb) return FieldTreasureKind.Silver;
        if (sgb == bronzeSgb) return FieldTreasureKind.Bronze;
        if (name.Contains('银') || name.Contains("silver", StringComparison.OrdinalIgnoreCase))
            return FieldTreasureKind.Silver;
        if (name.Contains('铜') || name.Contains("bronze", StringComparison.OrdinalIgnoreCase))
            return FieldTreasureKind.Bronze;
        return FieldTreasureKind.Unknown;
    }

    private string ActualTreasureLabel => ActualTreasureName;

    private static float HorizontalDistance(Vector3 left, Vector3 right) =>
        MathF.Sqrt(HorizontalDistanceSquared(left, right));

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return x * x + z * z;
    }
}

public readonly record struct TreasureSnapshot(ulong GameObjectId, Vector3 Position);

public enum FieldTreasureKind
{
    Unknown,
    Bronze,
    Silver
}

public readonly record struct FieldTreasureSnapshot(
    ulong GameObjectId,
    Vector3 Position,
    FieldTreasureKind Kind);
