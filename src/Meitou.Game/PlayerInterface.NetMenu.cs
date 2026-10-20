using Meitou.Data.Ui;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>The Esc menu's multiplayer rows and the players window (docs/meitou-ui.md "Multiplayer"). Hooks only: package U5 fills them.</summary>
sealed partial class PlayerInterface
{
    void BindNetMenu(UiBindings b) { }
    void AddNetMenuContent(List<UiControl> controls) { }
    /// <summary>An Esc menu press: true when a multiplayer row (or the multiplayer meaning of Exit) took it.</summary>
    bool NetMenuAction(MyGuiEvent e) => false;
    void RegisterPlayersScreen() { }
    void BindPlayersScreen(UiBindings b) { }
    void AddPlayersContent(List<UiControl> controls) { }
    void PlayersAction(MyGuiEvent e) { }
}
