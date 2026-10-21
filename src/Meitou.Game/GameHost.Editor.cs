using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Simulation.Editing;

namespace Meitou.Game;

/// <summary>
/// The character editor's host side (docs/characters.md "Character editor"): its data and preview source, opening it for a new game's squad once the world is shown,
/// and its scene, which replaces the world while the editor is open.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The new game's squad, waiting for the loading screen to lift before the editor opens on it.</summary>
    List<CharacterId>? pendingCreation;

    void ConfigureEditor()
    {
        if (popData is not { } data) return;
        player.EditorData = (data.Db, install.Root);
        player.PreviewSource = (recordId, loadout) => data.Appearances?.Create(recordId, loadout);
        player.PreviewReady = look => gpu.Characters?.IsPrepared(look) ?? true;
    }

    /// <summary>Opens the editor on the new game's squad once the world shows and the squad is in the snapshot.</summary>
    void OpenPendingEditor()
    {
        if (pendingCreation is not { } squad || sceneLoad is not null || warming) return;
        if (!squad.All(id => session.CurrentSnapshot.Characters.Any(c => c.Id == id))) return;
        pendingCreation = null;
        creationWarmOwed = true;
        if (!player.OpenEditor(EditKind.Creation, squad)) session.World.Commands.Enqueue(new FinishCreation { Tick = session.World.Tick });
    }

    /// <summary>The world has not been warmed up yet: the new game's editor came first (see <see cref="CoveredFrame"/>).</summary>
    bool creationWarmOwed;

    /// <summary>
    /// Once the new game's editor has closed: the edits are applied by a tick, then the loading screen comes back while the world warms up, as a load without an editor does.
    /// </summary>
    void ResumeAfterCreation()
    {
        if (!creationWarmOwed || pendingCreation is not null || player.EditorOpen || sceneLoad is not null) return;
        creationWarmOwed = false;
        if (loading is null) return;
        if (session.Mode == Meitou.Engine.SessionMode.Local) session.World.RunTick();   // a client's edits are the host's to apply
        loading.Restart();
        loadingSteps = SceneSteps;
        BeginWarm();
    }

    /// <summary>
    /// While the editor is open the world camera stays on the edited character, so the world around it is there when the editor closes; what the editor shows is its own scene
    /// (<see cref="DrawEditorStage"/>).
    /// </summary>
    void EditorCamera()
    {
        if (player.EditorFocus is not { } focus || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } c) return;
        camera.Target = DrawnPosition(c) + new Vector3(0, 10, 0);
    }

    /// <summary>Where the character stands on the picture, in normalised device units: the middle of the space between the editor's two panels (the left one ends at 16 % of the width, the right one starts at 70 %), not of the window.</summary>
    const float EditorStageShift = (0.5f * (0.161458f + 0.7f)) * 2 - 1;

    /// <summary>
    /// The frame of the character editor, in place of the world's: the original draws the edited character alone, lit plainly, over the plain ground texture
    /// (compositor <c>Kenshi_Character_Editor</c>, docs/characters.md). The renderer draws the character into the stage picture from the editor's orbit; the interface puts
    /// <c>SandyGround.dds</c> and the picture behind its panels (<c>PlayerInterface.EditorBackdrop</c>).
    /// </summary>
    void DrawEditorStage(int width, int height)
    {
        if (gpu.Characters is not { } characters) return;
        if (player.EditorView() is not { } view || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == view.Character) is not { } c) { characters.StageRequest = null; return; }
        characters.StageRequest = new EditorStageRequest(DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance, view.Head, camera.FieldOfView, EditorStageShift);
        characters.SetView(width, height, camera.FieldOfView);
        characters.Update(DrawnPosition(c));
        characters.DrawStage();
        if (characters.StageTexture is { } stage) player.SetUiTexture(PlayerInterface.StageTexture, stage.Texture, stage.Sampled);
    }
}
