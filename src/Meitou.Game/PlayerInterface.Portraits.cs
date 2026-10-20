using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The squad bar's portraits (both UI modes; docs/game/ui-screens.md): a cell per member as the original's <c>Kenshi_PortraitCharacter</c> lays it out (the
/// background, the portrait the renderer draws into its atlas, the name along the bottom, the frame that is the button), left to right on the squad tab's page.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The ImageBox texture name of the renderer's portrait atlas (<see cref="CharacterRenderer.PortraitAtlas"/>).</summary>
    public const string PortraitTexture = "meitou:portraits";
    const int PortraitCells = CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitRows;
    /// <summary>The cell layout's size (Kenshi_PortraitCharacter: 136 x 136); the parts below are placed in its pixels.</summary>
    const float PortraitLayoutSize = 136;

    /// <summary>The atlas cells after the squad's: the conversation window's speaker and player character.</summary>
    public const int DialogueNpcCell = PortraitCells - 2, DialoguePlayerCell = PortraitCells - 1;

    /// <summary>The characters the squad bar shows, the active squad's in order: the first atlas cells (<see cref="PortraitMembers"/>).</summary>
    public IReadOnlyList<CharacterSnapshot> SquadBarMembers()
    {
        var byId = session.CurrentSnapshot.Characters.Where(Mine).ToDictionary(c => c.Id);
        return [.. ActiveSquadMembers().Select(byId.GetValueOrDefault).OfType<CharacterSnapshot>().Take(DialogueNpcCell)];
    }

    /// <summary>
    /// The characters whose portraits the host draws, each into the atlas cell of its index: the squad bar's first, then, while the squads screen is open, the other
    /// squads' members in order. As many as there are cells before the conversation's (30, CONSTANTS <c>max faction size</c>); a modded faction past that shows nameless cells.
    /// </summary>
    public IReadOnlyList<CharacterSnapshot> PortraitMembers()
    {
        var bar = SquadBarMembers();
        if (!SquadsShown) return bar;   // the others are drawn (and posed out of range) only while the squads screen shows them
        var byId = session.CurrentSnapshot.Characters.Where(Mine).ToDictionary(c => c.Id);
        var shown = bar.Select(c => c.Id).ToHashSet();
        var rest = session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).Where(id => shown.Add(id)).Select(byId.GetValueOrDefault).OfType<CharacterSnapshot>();
        return [.. bar.Concat(rest).Take(DialogueNpcCell)];
    }

    /// <summary>The speaker and the player character of the open conversation, whose portraits the host draws into <see cref="DialogueNpcCell"/> and <see cref="DialoguePlayerCell"/>.</summary>
    public (CharacterId Npc, CharacterId Player)? DialoguePortraitCharacters() =>
        session.CurrentSnapshot.Conversation is { } talk ? (talk.Speaker.IsNone ? talk.Npc : talk.Speaker, talk.Player) : null;

    /// <summary>The number that turns a character's portrait camera (<see cref="CharacterSnapshot.PortraitSerial"/>; 0 for one not in the snapshot).</summary>
    public uint PortraitSerial(CharacterId id) => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.PortraitSerial ?? 0;

    /// <summary>The conversation window's two portraits over the layout's <c>NpcPortrait</c> and <c>PlayerPortrait</c> frames.</summary>
    void AddDialoguePortraits(List<UiControl> controls)
    {
        foreach (var (name, cell) in new[] { ("NpcPortrait", DialogueNpcCell), ("PlayerPortrait", DialoguePlayerCell) })
        {
            if (controls.FirstOrDefault(c => c.Name == name) is not { } frame) continue;
            int column = cell % CharacterRenderer.PortraitColumns, row = cell / CharacterRenderer.PortraitColumns, size = CharacterRenderer.PortraitSize;
            controls.Add(new($"{name}-image", "ImageBox", frame.Bounds, "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = FormattableString.Invariant($"{column * size} {row * size} {size} {size}") },
                Vector4.Zero, Pickable: false, Clip: frame.Clip));
        }
    }

    /// <summary>Gives the interface a texture drawn at run time (the portrait atlas) under <paramref name="name"/>.</summary>
    public void SetUiTexture(string name, Texture texture, SampledTexture sampled) => uiPainter?.SetTexture(name, texture, sampled);

    void AddSquadPortraits(List<UiControl> controls, UiRect panel)
    {
        var members = SquadBarMembers();
        if (members.Count == 0) return;
        // The tab's page (the layout's TabItem rectangle inside SquadPanel).
        var page = new UiRect(panel.X + panel.Width * 0.0183727f, panel.Y + panel.Height * 0.206897f, panel.Width * 0.965879f, panel.Height * 0.737069f);
        // The original leaves space below portraits instead of filling the tab page (docs/game/ui-screens.md); 0.76 matches its frame size (Observed).
        float size = Math.Min(page.Height * 0.76f, page.Width / members.Count), k = size / PortraitLayoutSize;
        var camera = session.Camera.Current;
        PrunePortraitFlashes(members);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            float left = page.X + i * size, top = page.Y;
            UiRect At(float x, float y, float w, float h) => new(left + x * k, top + y * k, w * k, h * k);
            // The layout's depths, back to front: background, back overlay, portrait, front overlay, name, frame.
            var state = PortraitStateOf(m, camera.Eye, camera.Target);
            controls.Add(PortraitImage($"portrait-back-{i}", At(0, 0, 135, 135), "Background", (state & PortraitState.Hurt) != 0 ? "Hurt" : "Normal", PortraitFlash(m.Id, state)));
            if (BackOverlayName(state) is { Length: > 0 } back) controls.Add(PortraitImage($"portrait-back-overlay-{i}", At(6, 5, 124, 124), "BackOverlay", back));
            int column = i % CharacterRenderer.PortraitColumns, row = i / CharacterRenderer.PortraitColumns, cell = CharacterRenderer.PortraitSize;
            var portrait = new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = FormattableString.Invariant($"{column * cell} {row * cell} {cell} {cell}") };
            if ((state & PortraitState.Faded) != 0) portrait["Alpha"] = "0.25";
            controls.Add(new($"portrait-{i}", "ImageBox", At(6, 6, 124, 123), "", "", "", true, portrait, Vector4.Zero));
            if (FrontOverlayName(state) is { Length: > 0 } front) controls.Add(PortraitImage($"portrait-front-overlay-{i}", At(6, 6, 124, 123), "FrontOverlay", front));
            controls.Add(new($"portrait-name-{i}", "TextBox", At(4, 4, 128, 126), m.Name, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["TextAlign"] = "HCenter Bottom", ["TextColour"] = "1 1 1", ["TextShadow"] = "true" }, Vector4.Zero, Pickable: false));
            controls.Add(new($"member-{i}", "Button", At(0, 0, 136, 136), "", "meitou:member", "Kenshi_PortraitFrameSkin", true,
                new Dictionary<string, string> { ["Checked"] = m.Selected ? "true" : "false" }, Vector4.Zero, Argument: i.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
