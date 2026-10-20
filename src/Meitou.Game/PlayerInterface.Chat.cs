using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>The chat line and its history (docs/meitou-ui.md "Multiplayer"). Hooks only: package U3 fills them.</summary>
sealed partial class PlayerInterface
{
    void RegisterChatScreen() { }
    void BindChatScreen(UiBindings b) { }
    void ChatAction(MyGuiEvent e) { }
    /// <summary>Once a frame: <paramref name="from"/> is the role of the last frame, <paramref name="events"/> the session's new events.</summary>
    void ChatFrame(NetRole from, IReadOnlyList<MultiplayerEvent> events) { }
    /// <summary>The OpenChat key outside windows: true when it opened the chat line.</summary>
    bool OpenChatKey(Key key) => false;
    /// <summary>A key while the chat line has the focus (Esc, history, Tab): true when taken.</summary>
    bool ChatEntryKey(Key key, bool shift) => false;
    void AddChatHistory(List<UiControl> controls) { }
}
