using Avarice.Structs;

namespace Avarice;

internal class PositionalDebugWindow : Window
{
    internal class Entry
    {
        public ulong Frame;
        public uint ActionId;
        public string ActionName;
        public bool InTable;
        public string TablePosition;
        public string Param2Display;
        public PositionalState Verdict;
        public string Target;
        public string Detail;
    }

    private static readonly List<Entry> Entries = new();
    private const int MaxEntries = 500;
    private static bool OnlyPositional = true;
    private static bool Paused;

    public PositionalDebugWindow() : base("Avarice 身位调试###AvaricePosDebug")
    {
        Size = new(780, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    internal static void Record(Entry e)
    {
        if (Paused) return;
        Entries.Add(e);
        if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
    }

    private static string BuildExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("frame\tid\taction\tin_table\tpos\tparam2\tverdict\ttarget\tdetail");
        foreach (var e in Entries)
        {
            if (OnlyPositional && !e.InTable) continue;
            sb.AppendLine($"{e.Frame}\t{e.ActionId}\t{e.ActionName}\t{e.InTable}\t{e.TablePosition}\t{e.Param2Display}\t{e.Verdict}\t{e.Target}\t{e.Detail?.Replace("\n", " | ")}");
        }
        return sb.ToString();
    }

    public override void Draw()
    {
        ImGuiEx.Text(ImGuiColors.DalamudYellow, "从自己的技能伤害包中捕获 param2（命中／失败表所使用的键值）。");
        ImGuiEx.TextWrapped("使用带身位技能的职业，分别从背面和侧面命中，再故意打错几次。将“判定”列与实际站位比较。带 * 的 param2 与表中的“命中”行匹配。可用“复制导出”分享原始值。");
        ImGui.Separator();

        ImGui.Checkbox("仅显示身位表中的技能", ref OnlyPositional);
        ImGui.SameLine();
        ImGui.Checkbox("暂停捕获", ref Paused);
        ImGui.SameLine();
        if (ImGui.Button("清空")) Entries.Clear();
        ImGui.SameLine();
        ImGuiEx.ButtonCopy("复制导出", BuildExport());
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        if (ImGui.BeginTable("##posdebug", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit, avail))
        {
            ImGui.TableSetupColumn("帧");
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("在表中");
            ImGui.TableSetupColumn("身位");
            ImGui.TableSetupColumn("param2");
            ImGui.TableSetupColumn("判定");
            ImGui.TableHeadersRow();

            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var e = Entries[i];
                if (OnlyPositional && !e.InTable) continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGuiEx.Text($"{e.Frame}");
                ImGui.TableNextColumn(); ImGuiEx.Text($"{e.ActionId}  {e.ActionName}");
                ImGui.TableNextColumn(); ImGuiEx.Text(e.InTable ? "是" : "否");
                ImGui.TableNextColumn(); ImGuiEx.Text(e.TablePosition ?? "");
                ImGui.TableNextColumn(); ImGuiEx.Text(e.Param2Display);
                ImGui.TableNextColumn();
                var col = e.Verdict == PositionalState.Success ? ImGuiColors.HealerGreen
                        : e.Verdict == PositionalState.Failure ? ImGuiColors.DalamudRed
                        : ImGuiColors.DalamudGrey;
                var verdict = e.Verdict == PositionalState.Success ? "成功"
                    : e.Verdict == PositionalState.Failure ? "失败" : "未知";
                ImGuiEx.Text(col, verdict);
            }
            ImGui.EndTable();
        }
    }
}
