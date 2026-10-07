using Lumina.Excel.Sheets;

namespace Avarice.ConfigurationWindow;

internal static class TabStatistics
{
    internal static void Draw()
    {
        Ui.PageTitle("统计", "当前配置方案的身位成功与失败记录。");
        DrawTotals();
        DrawEncounter();
    }

    private static void DrawTotals()
    {
        Ui.SectionLabel("总计");
        var hasData = P.currentProfile.Stats.Any(x => x.Value.Hits + x.Value.Missed > 0);
        if (!hasData)
        {
            ImGuiEx.Text(Ui.Muted, "当前配置方案尚无身位记录。");
            return;
        }

        ImGui.BeginTable("##table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit, new Vector2(ImGui.GetContentRegionAvail().X, 0));
        ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("成功");
        ImGui.TableSetupColumn("总数");
        ImGui.TableSetupColumn("成功率");
        ImGui.TableHeadersRow();
        var total = new Stats();
        foreach (var x in P.currentProfile.Stats)
        {
            DrawStatsRow(x.Key, x.Value);
            total.Hits += x.Value.Hits;
            total.Missed += x.Value.Missed;
        }
        if (total.Hits > 0 || total.Missed > 0)
            DrawStatsRow(0, total, "总计");
        ImGui.EndTable();

        if (ImGui.SmallButton("清除数据（按住 Shift+Ctrl）"))
        {
            if (ImGui.GetIO().KeyShift && ImGui.GetIO().KeyCtrl)
                P.currentProfile.Stats = new();
        }
    }

    private static void DrawEncounter()
    {
        var x = P.currentProfile.CurrentEncounterStats;
        var total = x.Hits + x.Missed;
        Ui.SectionLabel(x.Finished ? "最近一次战斗" : "当前战斗");

        if (total == 0)
        {
            ImGuiEx.Text(Ui.Muted, "暂无战斗数据。使用有身位要求的技能后将显示记录。");
            return;
        }

        var success = (int)(100f * x.Hits / total);
        ImGuiEx.Text($"成功：{x.Hits}/{total} — ");
        ImGui.SameLine(0, 0);
        ImGuiEx.Text(Util.GetParsedColor(success), $"{success}%");

        if (ImGui.SmallButton("清除数据"))
            P.currentProfile.CurrentEncounterStats = new();
        ImGui.SameLine();
        if (x.Finished)
        {
            ImGuiEx.Text(ImGuiColors.DalamudRed, "下次使用身位技能时将重置统计。");
        }
        else if (ImGui.SmallButton("结算"))
        {
            P.currentProfile.CurrentEncounterStats.Finished = true;
        }
    }

    private static void DrawStatsRow(uint job, Stats x, string colName = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGuiEx.Text(colName ?? Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job)?.Name.ToString());
        ImGui.TableNextColumn();
        ImGuiEx.Text($"{x.Hits}");
        ImGui.TableNextColumn();
        var total = x.Hits + x.Missed;
        ImGuiEx.Text($"{total}");
        ImGui.TableNextColumn();
        var success = (int)(100f * x.Hits / (float)total);
        ImGuiEx.Text(Util.GetParsedColor(success), $"{success}%");
    }
}
