using System.Numerics;
using CrescentCompass.Configuration;
using CrescentCompass.Core;
using CrescentCompass.Integrations;
using CrescentCompass.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CrescentCompass.Rendering;

public sealed unsafe class SceneOverlayRenderer
{
    private readonly PluginConfiguration configuration;
    private readonly TreasureTracker tracker;
    private readonly OccultEventTracker eventTracker;
    private readonly NavigationService navigationService;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly VNavmeshIpc vnavmesh;
    private readonly Dictionary<(uint Id, int X, int Z), (Vector3? Position, long UpdatedAt)> groundPositions = [];
    private readonly List<(Vector2 Minimum, Vector2 Maximum)> occupiedLabels = [];

    public SceneOverlayRenderer(
        PluginConfiguration configuration,
        TreasureTracker tracker,
        OccultEventTracker eventTracker,
        NavigationService navigationService,
        IGameGui gameGui,
        ICondition condition,
        IObjectTable objectTable,
        VNavmeshIpc vnavmesh)
    {
        this.configuration = configuration;
        this.tracker = tracker;
        this.eventTracker = eventTracker;
        this.navigationService = navigationService;
        this.gameGui = gameGui;
        this.condition = condition;
        this.objectTable = objectTable;
        this.vnavmesh = vnavmesh;
    }

    public void Draw()
    {
        if (!tracker.IsSupportedTerritory ||
            configuration.HideIndicatorsInCombat && condition[ConditionFlag.InCombat] ||
            tracker.PlayerPosition is not { } player)
            return;

        occupiedLabels.Clear();
        var draw = ImGui.GetBackgroundDrawList();

        if (navigationService.IsRecordingCustomRoute &&
            navigationService.RecordingDestination is { } routeDestination)
        {
            var ground = ResolveDisplayPosition(0x7fffffffu, routeDestination, player);
            var resolved = ground ?? new Vector3(routeDestination.X, player.Y, routeDestination.Z);
            DrawWorldLabel(draw, resolved,
                $"路线录制终点\n{DistanceAndHeight(player, resolved)}",
                Pack(255, 205, 70, 255), 1.2f);
        }

        if (configuration.ShowMonsterAggroRanges)
            DrawMonsterAggroRanges(draw, player);

        if (configuration.ShowFieldTreasureIndicators && !tracker.IsTreasureHuntActive)
        {
            foreach (var fieldChest in tracker.FieldTreasures
                         .Where(item => HorizontalDistance(player, item.Position) <= configuration.FieldTreasureIndicatorRadius)
                         .OrderBy(item => HorizontalDistance(player, item.Position)))
            {
                var title = fieldChest.Kind switch
                {
                    FieldTreasureKind.Silver => "银宝箱",
                    FieldTreasureKind.Bronze => "铜宝箱",
                    _ => "普通宝箱"
                };
                var color = fieldChest.Kind switch
                {
                    FieldTreasureKind.Silver => Pack(215, 230, 245, 255),
                    FieldTreasureKind.Bronze => Pack(224, 145, 70, 255),
                    _ => Pack(245, 205, 95, 255)
                };
                DrawWorldLabel(draw, fieldChest.Position,
                    $"{title}\n{DistanceAndHeight(player, fieldChest.Position)}", color, 1.1f);
            }
        }

        if (configuration.ShowEventSceneIndicators)
        {
            foreach (var activeEvent in eventTracker.ActiveEvents
                         .Where(item => item.Kind is OccultEventKind.Fate or
                             OccultEventKind.CriticalEngagement or OccultEventKind.MagicPot)
                         .Select(item => (Event: item,
                             Distance: HorizontalDistance(player, item.Position)))
                         .Where(item => item.Distance <= configuration.IndicatorRadius)
                         .OrderBy(item => item.Distance))
            {
                var item = activeEvent.Event;
                var ground = ResolveDisplayPosition(0x80000000u | item.DataId, item.Position, player);
                var resolved = ground ?? new Vector3(item.Position.X,
                    MathF.Abs(item.Position.Y) > 0.01f ? item.Position.Y : player.Y, item.Position.Z);
                var kind = item.Kind switch
                {
                    OccultEventKind.CriticalEngagement => "CE",
                    OccultEventKind.MagicPot => "魔法罐",
                    _ => "FATE"
                };
                var color = item.Kind switch
                {
                    OccultEventKind.CriticalEngagement => Pack(255, 205, 70, 255),
                    OccultEventKind.MagicPot => Pack(115, 245, 145, 255),
                    _ => Pack(90, 220, 240, 255)
                };
                var reward = string.IsNullOrWhiteSpace(item.RewardTag) ? string.Empty : $" {item.RewardTag}";
                DrawWorldLabel(draw, resolved,
                    $"{kind} · {item.Name}{reward}\n{DistanceAndHeight(player, resolved)}\n{item.StateText}",
                    color, item.Kind == OccultEventKind.CriticalEngagement ? 1.12f : 1.05f);
            }
        }

        if (configuration.ShowCandidateIndicators && tracker.Session.AcceptedHints.Count > 0)
        {
            var candidates = tracker.VisibleCandidates
                .Where(candidate => HorizontalDistance(player, candidate.Position) <= configuration.IndicatorRadius)
                .OrderBy(candidate => HorizontalDistance(player, candidate.Position))
                .Take(configuration.MaxSceneCandidates)
                .ToList();
            if (tracker.FocusedCandidate is { } focused && candidates.All(candidate => candidate.Id != focused.Id) &&
                HorizontalDistance(player, focused.Position) <= configuration.IndicatorRadius)
            {
                if (candidates.Count >= configuration.MaxSceneCandidates) candidates.RemoveAt(candidates.Count - 1);
                candidates.Add(focused);
            }

            foreach (var candidate in candidates)
            {
                var ground = ResolveDisplayPosition(candidate.Id, candidate.Position, player);
                var position = ground ?? new Vector3(candidate.Position.X, player.Y, candidate.Position.Z);
                var isFocused = tracker.FocusedCandidate?.Id == candidate.Id;
                var height = ground is { } resolved
                    ? $"{resolved.Y - player.Y:+0;-0;0}m"
                    : "未知";
                DrawWorldLabel(draw, position,
                    "宝藏候选" +
                    $"\n{HorizontalDistance(player, position):F0}m · 高差 {height}",
                    isFocused ? Pack(255, 205, 70, 255) : Pack(90, 220, 240, 240), isFocused ? 1.15f : 1f);
            }
        }

        if (configuration.ShowTreasureIndicators && tracker.ConfirmedTreasure is { } treasure)
            DrawWorldLabel(draw, treasure.Position, tracker.Session.Round >= 2 ? "第二次机会宝藏" : "魔法罐宝藏",
                Pack(110, 255, 120, 255), 1.2f);
    }

    private void DrawMonsterAggroRanges(ImDrawListPtr draw, Vector3 player)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero) return;
        var playerBattleChara = (BattleChara*)(void*)localPlayer.Address;
        var manager = CharacterManager.Instance();
        if (playerBattleChara == null || manager == null) return;

        var playerLevel = playerBattleChara->ForayInfo.Level;
        var scanRangeSquared = configuration.AggroScanRange * configuration.AggroScanRange;
        for (var index = 0; index < 100; index++)
        {
            var monster = manager->BattleCharas[index].Value;
            if (monster == null || monster == playerBattleChara || monster->NameId == 0 ||
                monster->IsDead() || monster->Health == 0 || !monster->GetIsTargetable() ||
                HorizontalDistanceSquared(player, monster->Position) > scanRangeSquared ||
                MathF.Abs(player.Y - monster->Position.Y) > configuration.AggroVerticalTolerance ||
                !OccultCrescentMonsterCatalog.TryGet(tracker.TerritoryId, monster->NameId, out var profile))
                continue;

            var monsterLevel = monster->ForayInfo.Level;
            if (monster->FateId != 0 || playerLevel != 0 && monsterLevel != 0 && monsterLevel < playerLevel)
                continue;

            var radius = Math.Max(0.5f,
                configuration.AggroEdgeRange + Math.Max(0f, monster->HitboxRadius) +
                configuration.AggroSafetyMargin);
            DrawAggroRange(draw, monster->Position, radius, monster->Rotation, profile.SenseType);
        }
    }

    private void DrawAggroRange(
        ImDrawListPtr draw,
        Vector3 center,
        float radius,
        float rotation,
        AggroSenseType senseType)
    {
        var halfAngle = OccultCrescentMonsterCatalog.DefaultHalfAngleDegrees(senseType) * MathF.PI / 180f;
        var isCone = halfAngle < MathF.PI - 0.01f;
        var startAngle = isCone ? rotation - halfAngle : 0f;
        var sweep = isCone ? halfAngle * 2f : MathF.Tau;
        var segments = isCone ? 24 : 48;
        var color = senseType == AggroSenseType.Sight
            ? Pack(255, 96, 72, 230)
            : Pack(255, 176, 55, 220);
        var shadow = Pack(0, 0, 0, 190);

        Vector2? first = null;
        Vector2? previous = null;
        if (isCone && gameGui.WorldToScreen(center, out var centerScreen))
        {
            first = centerScreen;
            previous = centerScreen;
        }

        for (var index = 0; index <= segments; index++)
        {
            var angle = startAngle + sweep * index / segments;
            var point = new Vector3(
                center.X + MathF.Sin(angle) * radius,
                center.Y,
                center.Z + MathF.Cos(angle) * radius);
            if (!gameGui.WorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }

            first ??= screen;
            if (previous is { } from)
            {
                draw.AddLine(from, screen, shadow, 4f);
                draw.AddLine(from, screen, color, 2f);
            }
            previous = screen;
        }

        if (first is not { } beginning || previous is not { } end) return;
        draw.AddLine(end, beginning, shadow, 4f);
        draw.AddLine(end, beginning, color, 2f);
    }

    private Vector3? ResolveDisplayPosition(uint id, Vector3 candidate, Vector3 player)
    {
        var now = Environment.TickCount64;
        var key = (id, (int)MathF.Round(candidate.X * 10f), (int)MathF.Round(candidate.Z * 10f));
        if (groundPositions.TryGetValue(key, out var cached) &&
            now - cached.UpdatedAt < (cached.Position.HasValue ? 30_000 : 5_000))
            return cached.Position;
        var probe = new Vector3(candidate.X, player.Y + 50f, candidate.Z);
        var resolved = vnavmesh.QueryPointOnFloor(probe, 3f) ??
                       vnavmesh.QueryNearestReachable(probe, 3f, 100f);
        groundPositions[key] = (resolved, now);
        return resolved;
    }

    private void DrawWorldLabel(ImDrawListPtr draw, Vector3 position, string text, uint color, float scale)
    {
        if (!gameGui.WorldToScreen(position, out var screen)) return;
        var size = ImGui.CalcTextSize(text) * scale;
        var topLeft = screen - new Vector2(size.X / 2f, size.Y + 14f);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var maximum = topLeft + size;
            if (occupiedLabels.All(item => maximum.X < item.Minimum.X || topLeft.X > item.Maximum.X ||
                                           maximum.Y < item.Minimum.Y || topLeft.Y > item.Maximum.Y)) break;
            topLeft.Y -= size.Y + 6f;
        }
        occupiedLabels.Add((topLeft - new Vector2(3f), topLeft + size + new Vector2(3f)));
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * scale;
        var outline = Pack(0, 0, 0, 230);
        foreach (var offset in new[] { new Vector2(-1.5f, 0f), new Vector2(1.5f, 0f),
                     new Vector2(0f, -1.5f), new Vector2(0f, 1.5f) })
            draw.AddText(font, fontSize, topLeft + offset, outline, text);
        draw.AddText(font, fontSize, topLeft, color, text);
        var anchor = new Vector2(Math.Clamp(screen.X, topLeft.X, topLeft.X + size.X), topLeft.Y + size.Y);
        draw.AddLine(anchor, screen, outline, 4f);
        draw.AddLine(anchor, screen, color, 2f);
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return MathF.Sqrt(x * x + z * z);
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return x * x + z * z;
    }

    private static string DistanceAndHeight(Vector3 player, Vector3 target) =>
        $"{HorizontalDistance(player, target):F0}m · 高差 {target.Y - player.Y:+0;-0;0}m";

    private static uint Pack(byte red, byte green, byte blue, byte alpha) =>
        (uint)(red | green << 8 | blue << 16 | alpha << 24);
}
