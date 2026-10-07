using Lumina.Excel.Sheets;

namespace Avarice.ConfigurationWindow;

internal static class TabProfiles
{
    internal static void Draw()
    {
        Ui.PageTitle("配置方案", "为方案命名、设为默认并分配职业。可从左侧列表切换方案。");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##namep", "配置方案名称……", ref P.currentProfile.Name, 100);

        if (ImGui.Button("新增"))
        {
            var prof = new Profile();
            P.config.Profiles.Add(prof);
            P.currentProfile = prof;
        }
        ImGui.SameLine();
        if (ImGui.Button("删除"))
        {
            if (P.config.Profiles.Count == 1)
            {
                Notify.Error("无法删除最后一个配置方案。");
            }
            else
            {
                P.config.Profiles.Remove(P.currentProfile);
                P.currentProfile = P.config.Profiles.FirstOr0(x => x.IsDefault);
            }
        }
        ImGui.SameLine();
        if (P.currentProfile.IsDefault)
        {
            ImGuiEx.Text(Ui.Muted, "这是默认配置方案。");
        }
        else if (ImGui.Button("设为默认"))
        {
            foreach (var x in P.config.Profiles)
                x.IsDefault = false;
            P.currentProfile.IsDefault = true;
        }

        Ui.SectionLabel("按职业分配配置方案");
        foreach (var x in Svc.Data.GetExcelSheet<ClassJob>().Where(j => j.JobIndex > 0))
        {
            ImGuiEx.Text($"{x.Name}：");
            ImGui.SameLine(120f * ImGuiHelpers.GlobalScale);
            ImGuiEx.SetNextItemFullWidth(-15);
            if (ImGui.BeginCombo($"##sel{x.RowId}", P.GetProfileForJob(x.RowId)?.Name ?? "<未分配>"))
            {
                if (ImGui.Selectable("取消分配"))
                    P.config.JobProfiles.Remove(x.RowId);
                foreach (var z in P.config.Profiles)
                {
                    if (ImGui.Selectable(z.Name))
                        P.config.JobProfiles[x.RowId] = z.GUID;
                }
                ImGui.EndCombo();
            }
        }
    }
}
