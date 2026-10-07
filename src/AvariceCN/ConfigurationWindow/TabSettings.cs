using Avarice.ConfigurationWindow.Player;
using Avarice.Data;
using Avarice.Positional;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Components;

namespace Avarice.ConfigurationWindow;

internal static class TabSettings
{
    private static readonly string[] SoundNames =
    {
        "<se.1>", "<se.2>", "<se.3>", "<se.4>", "<se.5>", "<se.6>", "<se.7>", "<se.8>",
        "<se.9>", "<se.10>", "<se.11>", "<se.12>", "<se.13>", "<se.14>", "<se.15>", "<se.16>"
    };

    internal static void DrawOverlays()
    {
        Ui.PageTitle("显示", "设置 Avarice 在目标、玩家和场地上绘制的内容。");
        if (!ImGui.BeginTabBar("##ovtabs"))
            return;
        if (ImGui.BeginTabItem("目标"))
        {
            DrawTarget();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("玩家"))
        {
            DrawPlayer();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("场地"))
        {
            DrawWorld();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private static void DrawTarget()
    {
        Ui.SectionLabel("绘制");
        Ui.CheckboxHelp("启用绘制", ref P.currentProfile.DrawingEnabled,
            "开关全部覆盖层绘制，也可以使用 /avarice draw 命令切换。");
        ImGui.SameLine();
        ImGuiEx.Text(new Vector4(0.7f, 0.7f, 1.0f, 1.0f), "(/avarice draw)");

        var prevOnlyPositional = P.config.OnlyDrawIfPositional;
        if (Ui.CheckboxHelp("仅对有身位要求的目标显示", ref P.config.OnlyDrawIfPositional,
                "启用后，仅在目标需要打身位时显示覆盖层。") &&
            prevOnlyPositional != P.config.OnlyDrawIfPositional)
        {
            Svc.PluginInterface.SavePluginConfig(P.config);
        }

        if (P.config.OnlyDrawIfPositional)
        {
            ImGui.Indent();
            Ui.CheckboxHelp("仍为无身位要求的目标显示距离指示器",
                ref P.currentProfile.MaxMeleeIgnorePositionalCheck,
                "为没有身位要求的目标保留敌人距离指示器。");
            Ui.CheckboxHelp("目标拥有无视身位效果时仍显示圆环",
                ref P.currentProfile.ShowPositionalWithoutCheckWhenNonPositionalBuffs,
                "目标拥有无视身位的状态时，仍显示距离指示器和身位圆环。");
            ImGui.Unindent();
        }

        Ui.SectionLabel("身位预判");
        Ui.FeatureHeader("启用身位预判扇区", ref P.currentProfile.EnableAnticipatedPie,
            ref P.currentProfile.AnticipatedPieSettings.DisplayCondition, "ant");
        if (P.currentProfile.EnableAnticipatedPie)
        {
            ImGui.Indent();
            Ui.FillColor("背面", "antr", ref P.currentProfile.AnticipatedPieSettings.Color);
            ImGui.SameLine(0, 16);
            ImGui.SetNextItemWidth(50f);
            ImGui.DragFloat("##anthr", ref P.currentProfile.AnticipatedPieSettings.Thickness, 0.1f, 0f, 10f);
            ImGui.SameLine();
            ImGuiEx.Text("粗细");

            Ui.FillColor("侧面", "antf", ref P.currentProfile.AnticipatedPieSettingsFlank.Color);
            ImGui.SameLine(0, 16);
            ImGui.SetNextItemWidth(50f);
            ImGui.DragFloat("##anthf", ref P.currentProfile.AnticipatedPieSettingsFlank.Thickness, 0.1f, 0f, 10f);
            ImGui.SameLine();
            ImGuiEx.Text("粗细");

            P.currentProfile.AnticipatedPieSettings.Fill = Vector4.Zero;
            P.currentProfile.AnticipatedPieSettingsFlank.Fill = Vector4.Zero;

            Ui.CheckboxHelp("真北生效时停用", ref P.currentProfile.AnticipatedDisableTrueNorth,
                "真北生效期间隐藏身位预判扇区。");

            var wrath = P.WrathComboWatcher.PluginInstalled;
            var rsr = RotationSolverWatcher.IsRSREnabled();
            if (wrath || rsr)
            {
                Ui.SectionLabel("预判来源");
                if (wrath)
                {
                    Ui.CheckboxHelp("使用 Wrath Combo 获取下一身位", ref P.currentProfile.UseWrathCombo,
                        rsr
                            ? "Wrath Combo 提供下一身位时优先使用，否则使用 Rotation Solver 或当前连击。"
                            : "Wrath Combo 提供下一身位时优先使用，否则使用当前连击。");
                }
                if (rsr)
                {
                    Ui.CheckboxHelp("使用 Rotation Solver 获取下一身位", ref P.currentProfile.UseRotationSolver,
                        wrath
                            ? "未使用 Wrath 或 Wrath 没有提示时使用 Rotation Solver，否则使用当前连击。"
                            : "Rotation Solver 提供下一身位时优先使用，否则使用当前连击。");
                }
            }

            Ui.SectionLabel("状态");
            DrawAnticipationStatus();

            if (ImGui.CollapsingHeader("职业选项"))
            {
                ImGuiEx.Text(Ui.Muted, "这些选项仅在 Avarice 读取当前连击时生效，不适用于 Wrath 或 Rotation Solver。");
                ImGuiEx.Text("忍者");
                Ui.CheckboxHelp("攻其不备就绪时显示背面", ref P.currentProfile.TrickAttack,
                    "攻其不备或百雷铳处于可用状态时显示背面身位。");
                Ui.CheckboxHelp("根据风缠同时显示两种身位", ref P.currentProfile.Kazematoi,
                    "拥有 1～3 档风缠时，同时显示背面和侧面身位。");
                ImGuiEx.Text("武士");
                Ui.CheckboxHelp("明镜止水期间隐藏", ref P.currentProfile.Meikyo,
                    "明镜止水生效期间隐藏预判扇区。");
                ImGuiEx.Text("钐镰客");
                ImGui.SameLine();
                ImGuiEx.Text("优先预判：");
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("绞决和缢杀均可用时优先显示哪个身位。");
                ImGui.SameLine();
                ImGui.RadioButton("背面", ref P.currentProfile.Reaper, 0);
                ImGui.SameLine();
                ImGui.RadioButton("侧面", ref P.currentProfile.Reaper, 1);
            }
            ImGui.Unindent();
        }

        Ui.SectionLabel("当前扇区");
        Ui.FeatureHeader("高亮当前所在扇区", ref P.currentProfile.EnableCurrentPie,
            ref P.currentProfile.CurrentPieSettings.DisplayCondition, "cur");
        if (P.currentProfile.EnableCurrentPie)
        {
            ImGui.Indent();
            Ui.FillColor("背面", "ca1", ref P.currentProfile.CurrentPieSettings.Fill);
            ImGui.SameLine(0, 16);
            Ui.FillColor("侧面", "ca1f", ref P.currentProfile.CurrentPieSettingsFlank.Fill);
            ImGui.Unindent();
        }

        Ui.SectionLabel("正面扇区");
        Ui.FeatureHeader("显示正面扇区", ref P.currentProfile.EnableFrontSegment,
            ref P.currentProfile.FrontSegmentIndicator.DisplayCondition, "front");
        if (P.currentProfile.EnableFrontSegment)
        {
            ImGui.Indent();
            Ui.FillColor("颜色", "ca2", ref P.currentProfile.FrontSegmentIndicator.Fill);
            ImGui.Unindent();
        }

        Ui.SectionLabel("敌人距离");
        Ui.FeatureHeader("敌人距离指示器", ref P.currentProfile.EnableMaxMeleeRing,
            ref P.currentProfile.MaxMeleeSettingsN.DisplayCondition, "mrd");
        if (P.currentProfile.EnableMaxMeleeRing)
        {
            ImGui.Indent();
            ImGui.Checkbox("战技距离（3 米）", ref P.currentProfile.Radius3);
            ImGui.SameLine();
            ImGui.Checkbox("自动攻击距离（2 米）", ref P.currentProfile.Radius2);
            ImGui.SameLine();
            ImGui.Checkbox("分隔线", ref P.currentProfile.DrawLines);
            Ui.ThicknessAndColor("mr", ref P.currentProfile.MaxMeleeSettingsN);
            ImGui.Unindent();
        }

        Ui.SectionLabel("近战距离");
        ImGui.SetNextItemWidth(50f);
        ImGui.DragFloat("能力技／战技距离", ref P.currentProfile.MeleeSkillAtk, 0.01f, 0.1f, 10f);
        ImGui.SameLine();
        ImGui.Checkbox("包含目标碰撞体积##skill", ref P.currentProfile.MeleeSkillIncludeHitbox);
        ImGui.SetNextItemWidth(50f);
        ImGui.DragFloat("近战自动攻击距离", ref P.currentProfile.MeleeAutoAtk, 0.01f, 0.1f, 10f);
        ImGui.SameLine();
        ImGui.Checkbox("包含目标碰撞体积##auto", ref P.currentProfile.MeleeAutoIncludeHitbox);
    }

    private static void DrawAnticipationStatus()
    {
        if (Svc.Targets.Target is not IBattleNpc)
        {
            Ui.StatusLine("没有目标", false);
            return;
        }

        var hint = Anticipation.Resolve((IBattleNpc)Svc.Targets.Target);
        var source = hint.Source switch
        {
            "wrath" => "Wrath",
            "rsr" => "Rotation Solver",
            "combo" => "当前连击",
            _ => null
        };

        if (source == null || hint.Segments == AnticipatedSegments.None)
        {
            Ui.StatusLine("无", false);
            return;
        }

        var segments = hint.Segments == AnticipatedSegments.Both
            ? "背面＋侧面"
            : hint.Segments.HasFlag(AnticipatedSegments.Rear) ? "背面" : "侧面";
        var text = $"{source} · {segments}";
        if (P.currentProfile.Debug && hint.Actions.Length > 0)
            text += $" ({string.Join(",", hint.Actions)})";
        Ui.StatusLine(text, true);
    }

    private static void DrawPlayer()
    {
        Ui.SectionLabel("玩家");
        Ui.FeatureHeader("玩家受击判定点", ref P.currentProfile.EnablePlayerDot,
            ref P.currentProfile.PlayerDotSettings.DisplayCondition, "dot",
            "在脚下显示代表受击判定的小点，建议使用默认粗细。");
        if (P.currentProfile.EnablePlayerDot)
        {
            ImGui.Indent();
            Ui.ThicknessAndColor("dot", ref P.currentProfile.PlayerDotSettings);
            ImGui.Unindent();
        }

        Ui.FeatureHeader("玩家攻击距离轮廓", ref P.currentProfile.EnablePlayerRing,
            ref P.currentProfile.PlayerRingSettings.DisplayCondition, "hitbox",
            "在玩家周围显示自动攻击可达范围。");
        if (P.currentProfile.EnablePlayerRing)
        {
            ImGui.Indent();
            Ui.ThicknessAndColor("hitbox", ref P.currentProfile.PlayerRingSettings);
            ImGui.Unindent();
        }

        Ui.SectionLabel("其他玩家");
        Ui.FeatureHeader("小队成员", ref P.currentProfile.PartyDot,
            ref P.currentProfile.PartyDotSettings.DisplayCondition, "dotp");
        if (P.currentProfile.PartyDot)
        {
            ImGui.Indent();
            Ui.ThicknessAndColor("dotp", ref P.currentProfile.PartyDotSettings);
            ImGui.Unindent();
        }

        Ui.FeatureHeader("所有玩家", ref P.currentProfile.AllDot,
            ref P.currentProfile.AllDotSettings.DisplayCondition, "dota");
        if (P.currentProfile.AllDot)
        {
            ImGui.Indent();
            Ui.ThicknessAndColor("dota", ref P.currentProfile.AllDotSettings);
            ImGui.Unindent();
        }
    }

    private static void DrawWorld()
    {
        Ui.SectionLabel("罗盘");
        BoxCompass.Draw();
        Ui.SectionLabel("场地中心");
        TabTank.Draw();
    }

    internal static void DrawFeedback()
    {
        Ui.PageTitle("反馈", "设置身位成功或失败时的反馈方式。");
        P.config.VisualFeedbackSettings ??= new VisualFeedbackSettings();
        P.config.AudioFeedbackSettings ??= new AudioFeedbackSettings();

        var visualSettings = P.config.VisualFeedbackSettings;
        var audioSettings = P.config.AudioFeedbackSettings;
        var vector = visualSettings.Mode == VisualFeedbackMode.Vector;

        ImGui.AlignTextToFramePadding();
        ImGuiEx.Text("视觉模式");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        var currentMode = (int)visualSettings.Mode;
        var modeNames = new[] { "矢量图标（勾／叉）", "游戏特效" };
        if (ImGui.Combo("##visualMode", ref currentMode, modeNames, modeNames.Length))
        {
            visualSettings.Mode = (VisualFeedbackMode)currentMode;
            Svc.PluginInterface.SavePluginConfig(P.config);
        }

        if (!vector)
            ImGuiEx.Text(Ui.Muted, "游戏特效模式包含内置音效。");

        ImGui.Spacing();
        if (ImGui.BeginTable("##fbcols", 2, ImGuiTableFlags.None))
        {
            ImGui.TableNextColumn();
            DrawFeedbackSide("身位成功", true, ref P.currentProfile.EnableVFXSuccess, ref P.currentProfile.EnableAudioSuccess,
                visualSettings, audioSettings, vector);
            ImGui.TableNextColumn();
            DrawFeedbackSide("身位失败", false, ref P.currentProfile.EnableVFXFailure, ref P.currentProfile.EnableAudioFailure,
                visualSettings, audioSettings, vector);
            ImGui.EndTable();
        }

        if (vector && (P.currentProfile.EnableVFXSuccess || P.currentProfile.EnableVFXFailure))
        {
            ImGui.SetNextItemWidth(150f);
            var iconSize = visualSettings.IconSize;
            if (ImGui.SliderFloat("图标大小", ref iconSize, 5f, 100f))
            {
                visualSettings.IconSize = iconSize;
                Svc.PluginInterface.SavePluginConfig(P.config);
            }
        }

        Ui.SectionLabel("聊天栏");
        ImGui.Checkbox("失败时输出", ref P.currentProfile.EnableChatMessagesFailure);
        ImGui.SameLine();
        ImGui.Checkbox("成功时输出", ref P.currentProfile.EnableChatMessagesSuccess);
        ImGui.Checkbox("战斗结束时输出汇总", ref P.currentProfile.Announce);
    }

    private static void DrawFeedbackSide(string title, bool hit, ref bool visual, ref bool audio,
        VisualFeedbackSettings visualSettings, AudioFeedbackSettings audioSettings, bool vector)
    {
        Ui.SectionLabel(title);
        ImGui.Checkbox(hit ? "视觉##hit" : "视觉##miss", ref visual);
        if (visual && vector)
        {
            ImGui.SameLine();
            var color = hit ? visualSettings.SuccessColor : visualSettings.FailureColor;
            if (ImGui.ColorEdit4(hit ? "##hitColor" : "##missColor", ref color, ImGuiColorEditFlags.NoInputs))
            {
                if (hit) visualSettings.SuccessColor = color;
                else visualSettings.FailureColor = color;
                Svc.PluginInterface.SavePluginConfig(P.config);
            }
        }

        if (vector)
        {
            ImGui.Checkbox(hit ? "音效##hit" : "音效##miss", ref audio);
            if (audio)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120f);
                var index = (int)(hit ? audioSettings.SuccessSoundId : audioSettings.FailureSoundId) - 1;
                if (index < 0 || index > 15)
                    index = hit ? 1 : 5;
                if (ImGui.Combo(hit ? "##hitSound" : "##missSound", ref index, SoundNames, 16))
                {
                    if (hit) audioSettings.SuccessSoundId = (uint)(index + 1);
                    else audioSettings.FailureSoundId = (uint)(index + 1);
                    Svc.PluginInterface.SavePluginConfig(P.config);
                }
            }
        }

        if (visual || (vector && audio))
        {
            if (ImGui.Button(hit ? "测试成功反馈" : "测试失败反馈"))
                PositionalFeedbackManager.TestFeedback(hit);
        }
    }

    internal static void DrawAdvanced()
    {
        Ui.PageTitle("高级", "渲染器选项及可选的 Splatoon 联动设置。");
        ImGuiEx.Text(new Vector4(1.0f, 0.8f, 0.0f, 1.0f), "警告：Pictomancy 在 Mac／Linux 上可能存在问题。");

        if (ImGui.Checkbox("在游戏界面下方渲染（Pictomancy）", ref P.config.UsePictomancyRenderer))
            Svc.PluginInterface.SavePluginConfig(P.config);
        ImGuiComponents.HelpMarker("启用后，覆盖层将在游戏原生界面下方而不是上方渲染。");

        if (P.config.UsePictomancyRenderer)
        {
            ImGui.Indent();
            if (ImGui.Checkbox("避让游戏原生界面", ref P.config.PictomancyClipNativeUI))
                Svc.PluginInterface.SavePluginConfig(P.config);
            ImGuiComponents.HelpMarker("自动裁剪与游戏原生界面重叠的绘制内容。");

            ImGui.SetNextItemWidth(150f);
            int maxAlpha = P.config.PictomancyMaxAlpha;
            if (ImGui.SliderInt("最大不透明度", ref maxAlpha, 0, 255))
            {
                P.config.PictomancyMaxAlpha = (byte)maxAlpha;
                Svc.PluginInterface.SavePluginConfig(P.config);
            }
            ImGuiComponents.HelpMarker("所有覆盖层绘制内容的最大不透明度（0～255）。");
            ImGui.Unindent();
        }

        if (Svc.PluginInterface.TryGetData<bool[]>("Splatoon.IsInUnsafeZone", out _))
        {
            Ui.SectionLabel("Splatoon");
            TabSplatoon.Draw();
        }
    }
}
