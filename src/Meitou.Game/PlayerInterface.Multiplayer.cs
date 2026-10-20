using Meitou.Data.Ui;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>The Multiplayer window (host and join; docs/meitou-ui.md "Multiplayer"). Hooks only: package U2 fills them.</summary>
sealed partial class PlayerInterface
{
    void RegisterMultiplayerScreen() { }
    void BindMultiplayerScreen(UiBindings b) { }
    void MultiplayerAction(MyGuiEvent e) { }
    void MultiplayerScreenFrame() { }
    /// <summary>Opens the Multiplayer window: from the title, or with <paramref name="runningGame"/> from the Esc menu to host the game running.</summary>
    public void OpenMultiplayer(bool runningGame) { }
    /// <summary>The new-game Begin button: true when the start was taken to host it (the Multiplayer window chose New game).</summary>
    bool BeginHostedNewGame(string startId) => false;
}
