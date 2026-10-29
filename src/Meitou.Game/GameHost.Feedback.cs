using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The feedback button's host side (PlayerInterface.Feedback.cs has the form): the frame after the press is drawn without the menus and kept as
/// the screenshot, then the form opens; Send saves the game into a temporary folder when asked and hands everything to <see cref="FeedbackSender"/>.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The next frame is the feedback screenshot (drawn without the menus, the settings and the debug overlays).</summary>
    bool feedbackShotRequested;
    bool feedbackSendRequested;
    /// <summary>The PNG of the last feedback screenshot, encoded off the frame thread; null without one.</summary>
    Task<byte[]>? feedbackPicture;
    Task<FeedbackResult>? feedbackSend;
    string feedbackStatus = "", feedbackStatusShown = "";

    void FeedbackAction(MyGuiEvent e)
    {
        if (e.Action == "feedback-open") feedbackShotRequested = true;
        else if (e.Action == "feedback-send") feedbackSendRequested = true;
    }

    /// <summary>The feedback frame was drawn: <paramref name="rgba"/> is its picture (rows from the top; null when it could not be read). The form opens.</summary>
    void FeedbackCaptured(byte[]? rgba, int width, int height)
    {
        feedbackShotRequested = false;
        player.FeedbackPrefs = config.Feedback ??= new();   // the same object: what the form changes is saved with the settings
        feedbackPicture = rgba is null ? null : Task.Run(() => FeedbackSender.Png(rgba, width, height));
        player.OpenFeedback(rgba is not null, SavesAvailable(out _) && session.World.Player.Exists ? null : "only in a game");
    }

    /// <summary>Once per frame, between ticks (a save may be made): a requested send starts, a finished one reports.</summary>
    void HandleFeedback()
    {
        if (feedbackSend is { IsCompleted: true } done)
        {
            feedbackSend = null;
            player.FeedbackSending = false;
            var result = done.Result;
            if (result.Error is { } error) player.FeedbackProgress(error, error: true);
            else
            {
                Console.WriteLine($"feedback  sent: issue #{result.Number} {result.Url}");
                player.FeedbackSent(result.Number, result.Url!);
            }
        }
        else if (feedbackSend is not null && Volatile.Read(ref feedbackStatus) is var status && status != feedbackStatusShown)
            player.FeedbackProgress(feedbackStatusShown = status);
        if (!feedbackSendRequested) return;
        feedbackSendRequested = false;
        if (feedbackSend is not null) return;
        var form = player.FeedbackValues();
        config.Save();   // the ticks and Discord name, kept for the next report
        string? saveFolder = null;
        Task? saveWritten = null;
        if (form.Save)
        {
            if (Busy()) { player.FeedbackProgress("A save or load is still in progress. Try again in a moment.", error: true); return; }
            saveFolder = Path.Combine(Path.GetTempPath(), $"meitou-feedback-{Guid.NewGuid():N}", $"feedback-{DateTime.Now:yyyyMMdd-HHmmss}");
            var before = saveWrite;
            QuickSaveNow(saveFolder, feedback: true);
            if (ReferenceEquals(saveWrite, before)) { player.FeedbackProgress("The game could not be saved (log.txt says why). Untick the save to send without it.", error: true); return; }
            saveWritten = saveWrite;
        }
        string[] mods = [.. Meitou.Data.LoadOrder.FromInstall(install).Entries.Select(e => e.Name)];
        string? log = form.Log && File.Exists(Path.Combine(AppContext.BaseDirectory, "log.txt")) ? Path.Combine(AppContext.BaseDirectory, "log.txt") : null;
        var package = new FeedbackPackage(form, BuildVersion.Tag, BuildVersion.Commit, FeedbackSender.Platform(), mods, form.Screenshot ? feedbackPicture : null,
            saveFolder, saveWritten, log);
        feedbackStatus = feedbackStatusShown = form.Save ? "Saving the game..." : "Sending...";
        player.FeedbackProgress(feedbackStatus);
        player.FeedbackSending = true;
        feedbackSend = Task.Run(() => FeedbackSender.Send(package, s => Volatile.Write(ref feedbackStatus, s)));
    }
}
