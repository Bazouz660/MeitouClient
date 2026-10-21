using Meitou.Data.Gameplay;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Ui;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>The item icons of the install: the files records name, then the images the game cached under <c>data/icons</c> (null without game data).</summary>
    ItemIcons? ItemIconsOfInstall() => scene.Database is { } db ? new(db, assets.Find, new ItemIconCache(Path.Combine(install.DataDirectory, "icons"))) : null;

    /// <summary>
    /// Gives the interface the install's item icons and, where the game has no saved picture, the character renderer's drawing of the item and the object renderer's of a building
    /// (from build mode's parts).
    /// </summary>
    void ConnectItemIcons()
    {
        player.SetItemIcons(ItemIconsOfInstall());
        player.SetRenderedIcons(name =>
        {
            if (UiPainter.TryParseRenderedIcon(name, out string record, out string material, out string colour))
                return gpu.Characters is { } characters && characters.TryGetIcon(new IconKey(record, material, colour), out var icon) ? icon : null;
            if (UiPainter.TryParseRenderedBuildingIcon(name, out string building) && gpu.Objects is { } objects && player.BuildHost is { } host)
                return objects.TryGetBuildingIcon(building, host.Parts(building), out var picture) ? picture : null;
            return null;
        });
    }
}
