using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Net.Session;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>Who a chat line is from: a player's words, the game's news, or a refused command.</summary>
enum ChatTone { Player, System, Error }

/// <summary><paramref name="At"/> is a <see cref="Stopwatch"/> timestamp; <paramref name="Player"/> -1 for the game's own lines.</summary>
readonly record struct ChatLine(long At, int Player, string Name, string Text, ChatTone Tone);

/// <summary>
/// The chat line and its history (docs/meitou-ui.md "Multiplayer", Chat). Engine choice; only in a session. Enter opens a non-modal one-line entry (never pauses), Enter or
/// SEND sends, Esc keeps the text as a draft. Own lines come back from the host as <see cref="MultiplayerEventKind.Chat"/> events, so every screen shows the host's order.
/// </summary>
sealed partial class PlayerInterface
{
    const int ChatMaxLines = 100, ChatMaxSent = 20, ChatClosedVisualLines = 8, ChatOpenVisualLines = 14;
    const double ChatShowSeconds = 12, ChatFadeSeconds = 2;
    const string ChatFont = "Kenshi_StandardFont_Medium";

    /// <summary>The entry sits under the history (<c>y = 0.47·H</c>); the HUD's InformationPanel starts at 0.5176·H (Verified, <c>Kenshi_MainPanel.layout</c>).</summary>
    const string ChatLayout = """
        <MyGUI type="Layout" version="3.2.0"><Widget type="Widget" skin="PanelEmpty" position_real="0.008 0.47 0.3 0.036" layer="Window" name="Root">
         <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0 0 0.8 1" align="Stretch" name="ChatEditBox"><Property key="MaxTextLength" value="200"/></Widget>
         <Widget type="Button" skin="Kenshi_Button2" position_real="0.81 0 0.19 1" align="Right VStretch" name="SendButton"><Property key="Caption" value="SEND"/></Widget>
        </Widget></MyGUI>
        """;

    readonly List<ChatLine> chatLines = [];
    readonly List<string> chatSent = [];
    int chatRecall = -1;
    string chatDraft = "";

    void RegisterChatScreen() => faithfulScreens.Register("chat", MyGuiReader.ParseLayout(ChatLayout), modal: false, centreRoot: false);

    void BindChatScreen(UiBindings b)
    {
        EnableButtons(b, "SendButton");
        b.Properties["SendButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
    }

    /// <summary>SEND (or Enter in the box): closes the entry, then sends the text or runs the command.</summary>
    void ChatAction(MyGuiEvent e)
    {
        if (e.Widget != "SendButton") return;
        string text = faithfulScreens.GetText("chat", "ChatEditBox").Trim();
        faithfulScreens.Close("chat");
        chatDraft = "";
        chatRecall = -1;
        if (text.Length == 0 || Net is not { } net) return;
        chatSent.Add(text);
        if (chatSent.Count > ChatMaxSent) chatSent.RemoveAt(0);
        if (text[0] == '/') RunChatCommand(net, text);
        else net.SendChat(text);
    }

    void AddChatLine(int player, string name, string text, ChatTone tone)
    {
        chatLines.Add(new(Stopwatch.GetTimestamp(), player, name, text, tone));
        if (chatLines.Count > ChatMaxLines) chatLines.RemoveAt(0);
    }

    void RunChatCommand(IMultiplayerView net, string text)
    {
        var command = ChatCommands.Parse(text);
        bool host = net.Role == NetRole.Host;
        var present = net.Players.Where(p => p.State != NetPlayerState.Away).ToList();
        switch (command.Verb)
        {
            case ChatVerb.Help:
                AddChatLine(-1, "", host ? "Commands: /players, /kick <player> [reason], /ban <player>, /help. A player is a name, the start of one, or #number."
                    : "Commands: /players, /help.", ChatTone.System);
                break;
            case ChatVerb.Players:
                foreach (var p in present)
                    AddChatLine(-1, "", p.Player == 0 ? $"#{p.Player} {p.Name} · HOST" : $"#{p.Player} {p.Name} · {p.RoundTripMs.ToString(CultureInfo.InvariantCulture)} ms", ChatTone.System);
                break;
            case ChatVerb.Kick or ChatVerb.Ban:
                bool kick = command.Verb == ChatVerb.Kick;
                if (!host) { AddChatLine(-1, "", kick ? "Only the host can kick players." : "Only the host can ban players.", ChatTone.Error); break; }
                if (command.Target.Length == 0) { AddChatLine(-1, "", kick ? "Usage: /kick <player> [reason]" : "Usage: /ban <player>", ChatTone.Error); break; }
                if (ChatCommands.Resolve(present, command.Target, out string error) is not { } target) { AddChatLine(-1, "", error, ChatTone.Error); break; }
                if (target.Player == net.LocalPlayer || target.Player == 0) { AddChatLine(-1, "", kick ? "You cannot kick yourself." : "You cannot ban yourself.", ChatTone.Error); break; }
                if (kick) net.Kick(target.Player, command.Rest);
                else net.Ban(target.Player);
                break;
            default:
                AddChatLine(-1, "", "Unknown command. Type /help.", ChatTone.Error);
                break;
        }
    }

    /// <summary>Once a frame: <paramref name="from"/> is the role of the last frame, <paramref name="events"/> the session's new events.</summary>
    void ChatFrame(NetRole from, IReadOnlyList<MultiplayerEvent> events)
    {
        var role = Net?.Role ?? NetRole.None;
        if (from == NetRole.None && role != NetRole.None)
        {
            chatLines.Clear();
            if (role == NetRole.Host && Net is { } hosting)
                AddChatLine(-1, "", $"Hosting on port {hosting.Rules.Port.ToString(CultureInfo.InvariantCulture)}. Others join with your address and this port.", ChatTone.System);
        }
        if (faithfulScreens.IsOpen("chat") && (role == NetRole.None || faithfulScreens.Focus is not { Screen: "chat" }))
        {
            chatDraft = faithfulScreens.GetText("chat", "ChatEditBox");   // clicked away (or the session ended): keep what was typed
            faithfulScreens.Close("chat");
        }
        foreach (var e in events)
        {
            bool you = e.Player == LocalPlayer;
            string who = you ? "You" : e.Name;
            string text;
            switch (e.Kind)
            {
                case MultiplayerEventKind.Chat:
                    AddChatLine(e.Player, e.Name, e.Text, ChatTone.Player);
                    LogMessage($"{e.Name}: {e.Text}");
                    continue;
                case MultiplayerEventKind.Joined: text = $"{who} joined the game."; break;
                case MultiplayerEventKind.Left: text = $"{who} left the game."; break;
                case MultiplayerEventKind.TimedOut: text = $"{who} lost the connection."; break;
                case MultiplayerEventKind.Kicked: text = $"{who} {(you ? "were" : "was")} kicked."; break;
                case MultiplayerEventKind.Banned: text = $"{who} {(you ? "were" : "was")} banned."; break;
                case MultiplayerEventKind.Paused: text = $"{who} paused the game."; break;
                case MultiplayerEventKind.Resumed: text = $"{who} resumed the game."; break;
                case MultiplayerEventKind.Speed: text = $"{who} set the speed to {e.Speed.ToString("0.#", CultureInfo.InvariantCulture)}×."; break;
                default: continue;
            }
            AddChatLine(-1, "", text, ChatTone.System);
            LogMessage(text);
        }
    }

    /// <summary>The OpenChat key outside windows: true when it opened the chat line.</summary>
    bool OpenChatKey(Key key)
    {
        if (!Multiplayer || TitleOpen || EditorOpen || !session.Bindings.Get(InputAction.OpenChat).Contains(Binding.Of(key))) return false;
        faithfulScreens.Open("chat");
        faithfulScreens.SetText("chat", "ChatEditBox", chatDraft);
        faithfulScreens.FocusBox("chat", "ChatEditBox");
        chatRecall = -1;
        return true;
    }

    /// <summary>A key while the chat line has the focus (Esc, history, Tab): true when taken.</summary>
    bool ChatEntryKey(Key key, bool shift)
    {
        if (faithfulScreens.Focus is not { Screen: "chat" }) return false;
        switch (key)
        {
            case Meitou.Engine.Input.Key.Escape:
                chatDraft = faithfulScreens.GetText("chat", "ChatEditBox");
                faithfulScreens.Close("chat");
                return true;
            case Meitou.Engine.Input.Key.Up or Meitou.Engine.Input.Key.Down:
                if (chatSent.Count == 0) return true;
                if (chatRecall < 0) chatDraft = faithfulScreens.GetText("chat", "ChatEditBox");
                chatRecall = key == Meitou.Engine.Input.Key.Up ? (chatRecall < 0 ? chatSent.Count - 1 : Math.Max(0, chatRecall - 1)) : (chatRecall + 1 < chatSent.Count ? chatRecall + 1 : -1);
                faithfulScreens.SetText("chat", "ChatEditBox", chatRecall < 0 ? chatDraft : chatSent[chatRecall]);
                return true;
            case Meitou.Engine.Input.Key.Tab:
                return true;   // swallowed: the focus never leaves the box without a click
            default:
                return false;
        }
    }

    /// <summary>
    /// The history at the bottom left above the entry: closed, lines younger than 12 s fade out over the last 2 (at most 8 rows); open, the last 14 rows. A player's line
    /// starts with its name in the player's colour. Sizes follow <see cref="AddSpeech"/>'s Kenshi_StandardFont_Medium.
    /// </summary>
    void AddChatHistory(List<UiControl> controls)
    {
        if (chatLines.Count == 0 || uiPainter is null) return;
        bool open = faithfulScreens.IsOpen("chat");
        float s = Height / 1080f, em = 20 * 50f / 72f * s, rowHeight = em * 1.25f, left = Width * 0.008f + 4 * s, bottom = Height * 0.47f - 6 * s, width = Width * 0.30f;
        float Measure(string text) => MeasureNet(ChatFont, text);
        long now = Stopwatch.GetTimestamp();
        var rows = new List<(ChatLine Line, string Text, bool First, float Alpha)>();
        foreach (var line in chatLines)
        {
            double age = Stopwatch.GetElapsedTime(line.At, now).TotalSeconds;
            float alpha = open ? 1 : (float)Math.Clamp((ChatShowSeconds - age) / ChatFadeSeconds, 0, 1);
            if (alpha <= 0) continue;
            string prefix = line.Tone == ChatTone.Player ? line.Name + ": " : "";
            bool first = true;
            foreach (string visual in Wrap(prefix + line.Text, Measure, width))
            {
                rows.Add((line, visual, first, alpha));
                first = false;
            }
        }
        int shown = Math.Min(rows.Count, open ? ChatOpenVisualLines : ChatClosedVisualLines);
        for (int i = 0; i < shown; i++)
        {
            var (line, text, first, alpha) = rows[rows.Count - shown + i];
            float y = bottom - (shown - i) * rowHeight;
            string a = alpha.ToString("R", CultureInfo.InvariantCulture);
            string name = "", rest = text;
            if (line.Tone == ChatTone.Player && first)
            {
                string prefix = line.Name + ":";
                if (text.StartsWith(prefix, StringComparison.Ordinal)) { name = prefix; rest = text[prefix.Length..].TrimStart(); }
                else { name = text; rest = ""; }   // a name longer than the row
            }
            float nameWidth = name.Length == 0 ? 0 : Measure(name + " x") - Measure("x");
            float restWidth = rest.Length == 0 ? 0 : Measure(rest);
            // The boxes get a few pixels more than the measured text (shadow, rounding), so the painter never shortens a line with an ellipsis.
            float slack = 8 * s;
            controls.Add(new($"chat-back-{i}", "Widget", new(left - 4 * s, y, nameWidth + restWidth + slack + 8 * s, rowHeight), "", "", "", true,
                new Dictionary<string, string> { ["Alpha"] = a }, new Vector4(0, 0, 0, 0.4f * alpha), Pickable: false));
            var font = new Dictionary<string, string> { ["FontName"] = ChatFont, ["TextAlign"] = "Left VCenter", ["TextShadow"] = "true", ["Alpha"] = a };
            if (name.Length > 0)
                controls.Add(new($"chat-name-{i}", "TextBox", new(left, y, nameWidth + slack, rowHeight), name, "", "", true,
                    new Dictionary<string, string>(font) { ["TextColour"] = PlayerColours.MyGui(PlayerInfo(line.Player)?.Colour ?? -1) }, Vector4.Zero, Pickable: false));
            if (rest.Length > 0)
                controls.Add(new($"chat-text-{i}", "TextBox", new(left + nameWidth, y, restWidth + slack, rowHeight), rest, "", "", true,
                    new Dictionary<string, string>(font) { ["TextColour"] = line.Tone switch { ChatTone.Error => BadColour, ChatTone.System => "0.85 0.81 0.72", _ => "0.92 0.89 0.83" } },
                    Vector4.Zero, Pickable: false));
        }
    }
}
