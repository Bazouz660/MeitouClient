using Meitou.Data.Ui;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>The session's notices, banners, indicators and alerts (docs/meitou-ui.md "Multiplayer"). Hooks only: package U5 fills them.</summary>
sealed partial class PlayerInterface
{
    /// <summary>Once a frame: <paramref name="from"/> is the role of the last frame, <paramref name="events"/> the session's new events.</summary>
    void StatusFrame(NetRole from, IReadOnlyList<MultiplayerEvent> events) { }
    void AddNetStatus(List<UiControl> controls) { }
    void BindNetHud(UiBindings b) { }
    /// <summary>The game host's report of an ended session (title DISCONNECTED once it reached play, else CONNECTION FAILED; <see cref="EndText"/>), shown over the title.</summary>
    public void ShowDisconnected(NetDisconnect reason, bool reachedPlay) { }
    void OpenPendingAlert() { }
}
