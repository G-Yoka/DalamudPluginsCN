using Dalamud.Interface.Utility.Raii;
using ECommons.MathHelpers;
using System.IO;

namespace Avarice.ConfigurationWindow;

internal class ConfigWindow : Window
{
    public ConfigWindow() : base($"{P.Name} 设置 - {P.currentProfile.Name.Default("未命名配置")}###AvariceConfig")
    {
        Size = new(800, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 480),
            MaximumSize = new Vector2(4096, 4096),
        };
    }

    public override void OnClose()
    {
        base.OnClose();
        Svc.PluginInterface.SavePluginConfig(P.config);
    }

    private int selectedSection = 0;
    private static readonly (string Label, FontAwesomeIcon Icon)[] Sections =
    {
        ("显示", FontAwesomeIcon.Crosshairs),
        ("反馈", FontAwesomeIcon.Bell),
        ("配置方案", FontAwesomeIcon.User),
        ("统计", FontAwesomeIcon.ChartBar),
        ("高级", FontAwesomeIcon.Cog),
        ("关于", FontAwesomeIcon.InfoCircle),
    };
    private static readonly string IconPath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "res", "avarice_icon.png");

    public override void Draw()
    {
        WindowName = $"{P.Name} 设置 - {P.currentProfile.Name.Default("未命名配置")}###AvariceConfig";
        using var colors = ImRaii.PushColor(ImGuiCol.CheckMark, Ui.Accent)
            .Push(ImGuiCol.SliderGrab, Ui.Accent)
            .Push(ImGuiCol.SliderGrabActive, Ui.AccentBright)
            .Push(ImGuiCol.Header, Ui.AccentA(0.26f))
            .Push(ImGuiCol.HeaderHovered, Ui.AccentA(0.42f))
            .Push(ImGuiCol.HeaderActive, Ui.AccentA(0.58f))
            .Push(ImGuiCol.Button, Ui.AccentA(0.32f))
            .Push(ImGuiCol.ButtonHovered, Ui.AccentA(0.50f))
            .Push(ImGuiCol.ButtonActive, Ui.AccentA(0.72f))
            .Push(ImGuiCol.FrameBgHovered, Ui.AccentA(0.20f))
            .Push(ImGuiCol.Tab, Ui.AccentA(0.22f))
            .Push(ImGuiCol.TabHovered, Ui.AccentA(0.42f))
            .Push(ImGuiCol.TabActive, Ui.AccentA(0.55f))
            .Push(ImGuiCol.SeparatorHovered, Ui.AccentA(0.50f));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f);
        DrawBody();
    }

    private void DrawBody()
    {
        using (var nav = ImRaii.Child("##avnav", new Vector2(156f * ImGuiHelpers.GlobalScale, 0), true))
        {
            if (nav)
                DrawNavRail();
        }

        ImGui.SameLine();

        using var body = ImRaii.Child("##avbody", new Vector2(0, 0), false);
        if (!body) return;
        ImGuiHelpers.ScaledDummy(2f);
        switch (selectedSection)
        {
            case 1: TabSettings.DrawFeedback(); break;
            case 2: TabProfiles.Draw(); break;
            case 3: TabStatistics.Draw(); break;
            case 4: TabSettings.DrawAdvanced(); break;
            case 5:
                Ui.PageTitle("关于", "Avarice 的作者、致谢与支持信息。");
                PunishLib.ImGuiMethods.AboutTab.Draw(Svc.PluginInterface.InternalName);
                break;
            case 100:
                Ui.PageTitle("日志", "插件内部日志。");
                InternalLog.PrintImgui();
                break;
            case 101:
                Ui.PageTitle("调试", "当前配置方案的开发者工具。");
                Debug();
                break;
            default: TabSettings.DrawOverlays(); break;
        }
    }

    private void DrawNavRail()
    {
        DrawNavLogo();

        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##avprofile", P.currentProfile.Name.Default("未命名配置")))
        {
            if (combo)
            {
                foreach (var prof in P.config.Profiles)
                {
                    if (ImGui.Selectable(prof.Name.Default("未命名配置"), prof.GUID == P.currentProfile.GUID))
                        P.currentProfile = prof;
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        for (var i = 0; i < Sections.Length; i++)
            DrawNavItem(i, Sections[i].Icon, Sections[i].Label);

        if (P.currentProfile.Debug)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawNavItem(100, FontAwesomeIcon.FileAlt, "日志");
            DrawNavItem(101, FontAwesomeIcon.Bug, "调试");
        }
    }

    private void DrawNavItem(int id, FontAwesomeIcon icon, string label)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = 32f * scale;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var selected = selectedSection == id;

        if (ImGui.InvisibleButton($"##nav{id}", new Vector2(width, height)))
            selectedSection = id;

        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var end = start + new Vector2(width, height);
        if (selected)
            draw.AddRectFilled(start, end, ImGui.GetColorU32(Ui.AccentA(0.45f)), 4f * scale);
        else if (hovered)
            draw.AddRectFilled(start, end, ImGui.GetColorU32(Ui.AccentA(0.22f)), 4f * scale);

        var iconStr = icon.ToIconString();
        float iconWidth;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconWidth = ImGui.CalcTextSize(iconStr).X;
            var iconY = start.Y + (height - ImGui.GetFontSize()) * 0.5f;
            draw.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), new Vector2(start.X + 10f * scale, iconY), ImGui.GetColorU32(ImGuiCol.Text), iconStr);
        }

        var textSize = ImGui.CalcTextSize(label);
        var textY = start.Y + (height - textSize.Y) * 0.5f;
        draw.AddText(new Vector2(start.X + 18f * scale + iconWidth, textY), ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    private void DrawNavLogo()
    {
        if (!ThreadLoadImageHandler.TryGetTextureWrap(IconPath, out var logo) || logo == null) return;
        var avail = ImGui.GetContentRegionAvail().X;
        var size = Math.Min(avail, 88f * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - size) * 0.5f);
        ImGui.Image(logo.Handle, new Vector2(size, size));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private int ActionOverride = 0;

    private void Debug()
    {
        if(ImGui.Button("打开身位调试窗口"))
        {
            P.positionalDebugWindow.IsOpen = true;
        }

        ImGui.Separator();
        ImGuiEx.Text(ImGuiColors.DalamudYellow, "Wrath 身位提示：");
        ImGuiEx.Text($"已安装：{P.WrathComboWatcher.PluginInstalled}");
        ImGuiEx.Text($"使用 Wrath：{P.currentProfile.UseWrathCombo}");
        ImGuiEx.Text($"IPC 可用：{P.WrathComboWatcher.Available}");
        if (!string.IsNullOrEmpty(P.WrathComboWatcher.LastError))
            ImGuiEx.Text(ImGuiColors.DalamudRed, $"IPC 错误：{P.WrathComboWatcher.LastError}");
        ImGuiEx.Text($"原始数据：[{P.WrathComboWatcher.LastWire}]");
        var wrathHint = P.WrathComboWatcher.CurrentHint;
        ImGuiEx.Text($"提示：方向={wrathHint.Direction} 技能={wrathHint.ActionId} GCD={wrathHint.GcdsUntil} 目标={wrathHint.TargetObjectId} 已满足={wrathHint.IsSatisfied}");
        ImGui.Separator();

        if(ImGui.CollapsingHeader("StaticAutoDetectRadiusData"))
        {
            ImGuiEx.Text(P.StaticAutoDetectRadiusData.Select(x => x.ToString()).Join("\n"));
        }
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "视觉反馈系统：");
            ImGui.Text("测试反馈：");
            if(ImGui.Button("显示成功"))
            {
                VisualFeedbackManager.DisplayFeedback(true);
            }
            ImGui.SameLine();
            if(ImGui.Button("显示失败"))
            {
                VisualFeedbackManager.DisplayFeedback(false);
            }
            ImGui.SameLine();
            if(ImGui.Button("隐藏"))
            {
                VisualFeedbackManager.RemoveFeedback();
            }
            ImGui.InputInt("技能覆盖测试", ref ActionOverride);
            if(ImGui.Button("设置技能覆盖"))
            {
                Svc.PluginInterface.GetOrCreateData("Avarice.ActionOverride", () => new List<uint>() { 0 })[0] = (uint)ActionOverride;
            }
            ImGuiEx.Text($"当前技能覆盖：{(Svc.PluginInterface.TryGetData<List<uint>>("Avarice.ActionOverride", out var data) ? data[0] : 0)}");
            ImGuiEx.Text($"连击：{P.memory.LastComboMove}");
            foreach(var x in Svc.Objects.LocalPlayer?.StatusList)
            {
                ImGuiEx.TextCopy($"{x.GameData.ValueNullable?.Name}: id={x.StatusId}, time={x.RemainingTime}");
            }

            ImGuiEx.Text("N. S. ");
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.SameLine(0, 0);
                ImGuiEx.Text(ImGuiColors.DalamudRed, FontAwesomeIcon.Heart.ToIconString());
            }
            ImGuiEx.Text($"目标是否有身位要求：{Svc.Targets.Target?.HasPositional()}");
            if(ImGui.Button("测试 IPC"))
            {
                Safe(TestIPC);
            }
        }
    }

    private void TestIPC()
    {
        var result = Svc.PluginInterface.GetIpcSubscriber<IntPtr, CardinalDirection>("Avarice.CardinalDirection").InvokeFunc(Svc.Targets.Target?.Address ?? IntPtr.Zero);
        Svc.Chat.Print(result.ToString());
    }
}
