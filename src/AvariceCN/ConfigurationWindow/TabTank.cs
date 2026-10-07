using Dalamud.Interface.Components;
using ECommons.Schedulers;
using Lumina.Excel.Sheets;

namespace Avarice.ConfigurationWindow
{
    internal static class TabTank
    {
        static string Filter = "";
        internal static void Draw()
        {
            var cur = ImGui.GetCursorPos();
            ImGui.PushFont(UiBuilder.IconFont);
            var text = FontAwesomeIcon.Heart.ToIconString();
            ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X);
            ImGuiEx.Text(EColor.RedBright, text);
            ImGui.PopFont();
            ImGuiEx.Tooltip("我是为她做的。\n很抱歉我并非最出色的那一个。\n但你是我的灵感来源。");
            ImGui.SetCursorPos(cur);
            Ui.CheckboxHelp("首领居中判定点", ref P.currentProfile.EnableTankMiddle,
                "在首领脚下显示一个点，帮助将其拉到场地中央。此选项按配置方案保存。");
            Ui.CheckboxHelp("任务场地中心点", ref P.currentProfile.EnableDutyMiddle,
                "在任务场地设定的中心位置显示一个点。此选项按配置方案保存。");
            if (P.currentProfile.EnableTankMiddle || P.currentProfile.EnableDutyMiddle)
            {
                ImGui.ColorEdit4("场地中心点颜色", ref P.config.DutyMidPixelCol, ImGuiColorEditFlags.NoInputs);
                ImGui.SetNextItemWidth(100f);
                ImGui.SliderFloat("居中判定距离", ref P.config.DutyMidRadius, 0.5f, 5f);
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("数值越小，首领越需要贴近场地中心才会显示居中颜色。");
                ImGui.ColorEdit4("已居中颜色", ref P.config.CenteredPixelColor, ImGuiColorEditFlags.NoInputs);
                ImGui.ColorEdit4("未居中颜色", ref P.config.UncenteredPixelColor, ImGuiColorEditFlags.NoInputs);
                ImGui.SetNextItemWidth(100f);
                ImGui.SliderFloat("判定点大小", ref P.config.CenterPixelThickness, 0.5f, 5f);
            }

            Ui.SectionLabel("任务场地中心覆盖");
            ImGuiEx.TextV("新增覆盖：");
            ImGui.SameLine();
            ImGuiEx.SetNextItemFullWidth();
            if(ImGui.BeginCombo("##addoverride", "请选择……"))
            {
                ImGui.InputTextWithHint("##fltr", "筛选", ref Filter, 100);
                foreach(var x in Svc.Data.GetExcelSheet<TerritoryType>())
                {
                    if (P.config.DutyMiddleOverrides.ContainsKey(x.RowId)) continue;
                    var cfc = x.ContentFinderCondition.ValueNullable?.Name.ToString();
                    if(cfc != null && cfc != "" && (Filter == "" || cfc.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (ImGui.Selectable($"{(P.StaticAutoDetectRadiusData.Contains(x.RowId)?"*":"")}{cfc}##{x.RowId}"))
                        {
                            P.config.DutyMiddleOverrides[x.RowId] = null;
                        }
                    }
                }
                ImGui.EndCombo();
            }
            if(P.config.DutyMiddleOverrides.Count > 0 && ImGui.BeginTable("TableOverrides", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("中心点覆盖");
                ImGui.TableSetupColumn(" ");
                ImGui.TableHeadersRow();
                foreach (var x in P.config.DutyMiddleOverrides.ToArray())
                {
                    ImGui.PushID((int)x.Key);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"{Svc.Data.GetExcelSheet<TerritoryType>().GetRow(x.Key).ContentFinderCondition.Value.Name}");
                    ImGui.TableNextColumn();
                    var isAuto = x.Value == null;
                    if(ImGui.Checkbox("自动", ref isAuto))
                    {
                        if (isAuto)
                        {
                            P.config.DutyMiddleOverrides[x.Key] = null;
                        }
                        else
                        {
                            P.config.DutyMiddleOverrides[x.Key] = Vector3.Zero;
                        }
                    }
                    if (!isAuto && x.Value != null)
                    {
                        var vector = x.Value.Value;
                        ImGui.SetNextItemWidth(200f);
                        ImGui.SameLine();
                        if (ImGui.DragFloat3("##input", ref vector, 0.1f))
                        {
                            P.config.DutyMiddleOverrides[x.Key] = vector;
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("+"))
                    {
                        P.config.DutyMiddleExtras.Add(new() { TerritoryType = x.Key });
                    }
                    for (int i = 0; i < P.config.DutyMiddleExtras.Count; i++)
                    {
                        var point = P.config.DutyMiddleExtras[i];
                        if (point.TerritoryType != x.Key) continue;
                        ImGui.PushID(i);
                        ImGui.SetNextItemWidth(200f);
                        ImGui.DragFloat3("##input", ref point.Position, 0.1f);
                        ImGui.SameLine();
                        if (ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                        {
                            var rem = i;
                            new TickScheduler(() => P.config.DutyMiddleExtras.RemoveAt(rem));
                        }
                        ImGui.PopID();
                    }
                    ImGui.TableNextColumn();
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                    {
                        P.config.DutyMiddleOverrides.Remove(x.Key);
                    }
                    ImGui.SameLine();
                    ImGuiEx.Text($" ");
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }

    }
}
