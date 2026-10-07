namespace Avarice;

internal static class PositionalFeedbackManager
{
    internal static void TriggerFeedback(bool success)
    {
        var profile = P.currentProfile;
        if (profile == null) return;

        // Visual feedback
        if ((success && profile.EnableVFXSuccess) || (!success && profile.EnableVFXFailure))
        {
            DisplayVisualFeedback(success);
        }

        // Audio feedback (only for Vector mode - GameVfx has built-in sounds)
        var mode = P.config.VisualFeedbackSettings?.Mode ?? VisualFeedbackMode.Vector;
        if (mode == VisualFeedbackMode.Vector)
        {
            if (success && profile.EnableAudioSuccess)
                AudioFeedbackManager.PlaySuccessSound();
            else if (!success && profile.EnableAudioFailure)
                AudioFeedbackManager.PlayFailureSound();
        }

        // Chat messages
        if (success && profile.EnableChatMessagesSuccess)
            Svc.Chat?.Print("身位成功！");
        else if (!success && profile.EnableChatMessagesFailure)
            Svc.Chat?.Print("身位失败！");

        P.RecordStat(!success);
    }

    internal static void TestFeedback(bool success)
    {
        var profile = P.currentProfile;
        if (profile == null) return;

        if ((success && profile.EnableVFXSuccess) || (!success && profile.EnableVFXFailure))
        {
            DisplayVisualFeedback(success);
        }

        // Audio feedback (only for Vector mode - GameVfx has built-in sounds)
        var mode = P.config.VisualFeedbackSettings?.Mode ?? VisualFeedbackMode.Vector;
        if (mode == VisualFeedbackMode.Vector)
        {
            if (success && profile.EnableAudioSuccess)
                AudioFeedbackManager.PlaySuccessSound();
            else if (!success && profile.EnableAudioFailure)
                AudioFeedbackManager.PlayFailureSound();
        }
    }

    private static void DisplayVisualFeedback(bool success)
    {
        var mode = P.config.VisualFeedbackSettings?.Mode ?? VisualFeedbackMode.Vector;

        switch (mode)
        {
            case VisualFeedbackMode.GameVfx:
                LockonManager.DisplayIcon(success);
                break;
            case VisualFeedbackMode.Vector:
            default:
                VisualFeedbackManager.DisplayFeedback(success);
                break;
        }
    }
}
