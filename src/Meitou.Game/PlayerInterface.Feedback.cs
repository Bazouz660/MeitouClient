using Meitou.Data.Ui;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>What the player filled in on the feedback form: the attachments are the ticked ones the game can make.</summary>
sealed record FeedbackForm(string Title, string Body, bool Screenshot, bool Save, bool Log);

/// <summary>
/// Meitou's feedback button, always at the top right in both UI modes, and the form it opens: a title, the text, and ticks for a screenshot
/// (taken when the button is pressed, without the menus), a save made when sending and log.txt. The host (GameHost.Feedback.cs) takes the
/// picture, makes the files and sends them to the reports service, which opens a GitHub issue (docs/play-reports.md "Feedback").
/// </summary>
sealed partial class PlayerInterface
{
    const string FeedbackScreen = "feedback", FeedbackButton = "MeitouFeedbackButton";
    const string FeedbackTitleBox = "TitleEditBox", FeedbackBodyBox = "BodyEditBox", FeedbackShotTick = "ScreenshotTickBox", FeedbackSaveTick = "SaveTickBox", FeedbackLogTick = "LogTickBox";
    /// <summary>The screens a feedback screenshot leaves out: the game menu and what it opens, and the form itself.</summary>
    static readonly HashSet<string> FeedbackShotHides = ["menu", "message", "save", "load", "import", "new-game", "file", FeedbackScreen];

    MyGuiLayout? feedbackButtonLayout;
    bool feedbackShot = true, feedbackSave = true, feedbackLog, feedbackHasShot, feedbackStatusError;
    string? feedbackSaveMissing;
    string feedbackStatus = "";

    /// <summary>This frame is the feedback screenshot: drawn without the menus, the form and the button.</summary>
    public bool ShotWithoutMenus { get; set; }
    /// <summary>A send is under way: the form's Send waits for it.</summary>
    public bool FeedbackSending { get; set; }

    static MyGuiLayout FeedbackButtonLayout() => MyGuiReader.ParseLayout(
        $"""<MyGUI type="Layout"><Widget type="Button" skin="Kenshi_Button1" position_real="0.9 0 0.1 0.03" layer="Info" name="{FeedbackButton}"/></MyGUI>""");

    static MyGuiLayout FeedbackLayout()
    {
        static string Tick(string tick, string y) =>
            $"""
            <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.03 {y} 0.05 0.054" name="{tick}"/>
            <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.09 {y} 0.88 0.054" name="lb{tick}"><Property key="TextAlign" value="Left VCenter"/></Widget>
            """;
        return MyGuiReader.ParseLayout($"""
            <MyGUI type="Layout">
              <Widget type="Window" skin="Kenshi_WindowCX" position_real="0 0 0.36 0.62" layer="Window" name="Root">
                <Property key="Caption" value="SEND FEEDBACK"/>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 0.02 0.94 0.1" name="lbIntro"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 0.13 0.94 0.045" name="lbTitleBox"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.03 0.175 0.94 0.065" name="{FeedbackTitleBox}"><Property key="MaxTextLength" value="100"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 0.255 0.94 0.045" name="lbBodyBox"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_WordWrap" position_real="0.03 0.3 0.94 0.3" name="{FeedbackBodyBox}">
                  <Property key="Static" value="false"/><Property key="MaxTextLength" value="5000"/><Property key="MultiLine" value="true"/><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/>
                </Widget>
                {Tick(FeedbackShotTick, "0.62")}
                {Tick(FeedbackSaveTick, "0.68")}
                {Tick(FeedbackLogTick, "0.74")}
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 0.8 0.94 0.085" name="StatusText"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/></Widget>
                <Widget type="Button" skin="Kenshi_Button1" position_real="0.53 0.895 0.215 0.08" name="SendButton"><Property key="DefaultAction" value="true"/></Widget>
                <Widget type="Button" skin="Kenshi_Button1" position_real="0.765 0.895 0.215 0.08" name="CancelButton"/>
              </Widget>
            </MyGUI>
            """);
    }

    /// <summary>The button at the top right, over everything; left out of the feedback screenshot and while the form is open.</summary>
    void AddFeedbackButton(List<UiControl> controls)
    {
        if (feedbackButtonLayout is null || ShotWithoutMenus || faithfulScreens.IsOpen(FeedbackScreen)) return;
        float scale = Height / 1080f, w = 150 * scale, h = 36 * scale;
        var b = new UiBindings();
        b.Positions[FeedbackButton] = new(Width - w - 8 * scale, 8 * scale, w, h);
        b.Text[FeedbackButton] = UpperCaption("Feedback");
        b.Actions[FeedbackButton] = "feedback-open";
        controls.AddRange(MyGuiPresentation.Arrange(feedbackButtonLayout, Width, Height, b, uiResources.Layers));
    }

    /// <summary>A press on the feedback button: the host takes the screenshot on the next frame and then opens the form.</summary>
    bool FeedbackButtonDown(IReadOnlyList<UiControl> controls, System.Numerics.Vector2 p)
    {
        if (controls.LastOrDefault(c => c.Name == FeedbackButton) is not { } button || !button.Bounds.Contains(p.X, p.Y)) return false;
        Sound("General_Click");
        ScreenAction?.Invoke(new(FeedbackScreen, FeedbackButton, "feedback-open"));
        return true;
    }

    /// <summary>Opens the form; <paramref name="screenshot"/>: a picture was taken; <paramref name="saveMissing"/>: why no save can be made, or null.</summary>
    public void OpenFeedback(bool screenshot, string? saveMissing)
    {
        feedbackHasShot = screenshot;
        feedbackSaveMissing = saveMissing;
        if (!FeedbackSending) { feedbackStatus = ""; feedbackStatusError = false; }
        faithfulScreens.Open(FeedbackScreen);
        UpdateMenuPause();
    }

    public FeedbackForm FeedbackValues() => new(faithfulScreens.GetText(FeedbackScreen, FeedbackTitleBox).Trim(), faithfulScreens.GetText(FeedbackScreen, FeedbackBodyBox).Trim(),
        feedbackShot && feedbackHasShot, feedbackSave && feedbackSaveMissing is null, feedbackLog);

    /// <summary>The send's progress or why it failed, under the ticks.</summary>
    public void FeedbackProgress(string text, bool error = false) => (feedbackStatus, feedbackStatusError) = (text, error);

    /// <summary>The issue is open: the form closes, emptied, and a message thanks the player (its first button copies the link).</summary>
    public void FeedbackSent(int number, string url)
    {
        faithfulScreens.SetText(FeedbackScreen, FeedbackTitleBox, "");
        faithfulScreens.SetText(FeedbackScreen, FeedbackBodyBox, "");
        (feedbackStatus, feedbackStatusError) = ("", false);
        faithfulScreens.Close(FeedbackScreen);
        Confirm("THANK YOU", $"Thanks for the feedback! It is now issue #{number} on GitHub.", () => ClipboardWrite?.Invoke(url), "COPY LINK", cancel: "OK");
        UpdateMenuPause();
    }

    void BindFeedbackScreen(UiBindings b)
    {
        void Enable(params string[] names) { foreach (string name in names) { b.Disabled.Remove(name); b.Actions[name] = name; } }
        void Tick(string name, bool on) => b.Properties[name] = new Dictionary<string, string> { ["StateSelected"] = on ? "true" : "false", ["Checked"] = on ? "true" : "false" };
        b.Text["Root"] = UpperCaption("Send feedback");
        b.Text["lbIntro"] = "Tell us what went wrong or what you think. It becomes a public issue on GitHub, with your Meitou version and load order.";
        b.Text["lbTitleBox"] = "Title";
        b.Text["lbBodyBox"] = "What happened";
        b.Text["lb" + FeedbackShotTick] = feedbackHasShot ? "Attach a screenshot of the game" : "Attach a screenshot (none was taken)";
        b.Text["lb" + FeedbackSaveTick] = feedbackSaveMissing is null ? "Save the game now and attach the save" : $"Attach a save ({feedbackSaveMissing})";
        b.Text["lb" + FeedbackLogTick] = "Attach log.txt (it can show folder names, such as your user name)";
        b.Text["StatusText"] = feedbackStatus;
        if (feedbackStatusError) b.Properties["StatusText"] = new Dictionary<string, string> { ["TextColour"] = BadColour };
        b.Text["SendButton"] = UpperCaption(FeedbackSending ? "Sending" : "Send");
        b.Text["CancelButton"] = UpperCaption("Close");
        Enable("CancelButton", FeedbackLogTick);
        if (!FeedbackSending && faithfulScreens.GetText(FeedbackScreen, FeedbackTitleBox).Trim().Length > 0) Enable("SendButton");
        if (feedbackHasShot) Enable(FeedbackShotTick);
        if (feedbackSaveMissing is null) Enable(FeedbackSaveTick);
        Tick(FeedbackShotTick, feedbackShot && feedbackHasShot);
        Tick(FeedbackSaveTick, feedbackSave && feedbackSaveMissing is null);
        Tick(FeedbackLogTick, feedbackLog);
    }

    void FeedbackFormAction(MyGuiEvent e)
    {
        if (e.Action != "text") Sound("General_Click");
        if (e.Widget == "CancelButton") faithfulScreens.Close(FeedbackScreen);
        else if (e.Widget == FeedbackShotTick) feedbackShot = !feedbackShot;
        else if (e.Widget == FeedbackSaveTick) feedbackSave = !feedbackSave;
        else if (e.Widget == FeedbackLogTick) feedbackLog = !feedbackLog;
        else if (e.Widget == "SendButton" && !FeedbackSending && FeedbackValues().Title.Length > 0) ScreenAction?.Invoke(new(FeedbackScreen, "SendButton", "feedback-send"));
        UpdateMenuPause();
    }
}
