using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The stealth indicators of a sneaking player character (docs/game/perception.md "Spotting a sneaker"): a short pointer towards each hostile NPC noticing (yellow) or
/// detecting (red) it, and a tag over its head tinted by its state, the original's name-tag colours (hidden blue, almost seen yellow, seen red).
/// </summary>
sealed partial class PlayerInterface
{
    // The original's pointer colours (FUN_1408586d0) and name-tag fallback colours (NameTagStealthHidden / AlmostSeen / Seen).
    static readonly Vector4 PointerNoticed = new(1, 0xdd / 255f, 0, 1), PointerDetected = new(0xdd / 255f, 0, 0, 1);
    static readonly Vector4 TagHidden = new(0.25f, 0.521f, 0.745f, 1), TagAlmostSeen = new(0.745f, 0.721f, 0.25f, 1), TagSeen = new(0.745f, 0.25f, 0.29f, 1);

    /// <summary>The original's name-tag range: 1.2e8 squared (about 10954 units) from the camera.</summary>
    const float NameTagRangeSquared = 1.2e8f;

    void DrawStealth(DebugOverlay o)
    {
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (c.Stealth is null) continue;
            var feet = where(c);
            // Pointers 12 units up, out towards the looker by min(d - 7, 15) (the original's figures).
            var up = feet + new Vector3(0, 12, 0);
            foreach (var w in c.Watchers)
            {
                var d = new Vector3(w.Position.X - feet.X, 0, w.Position.Z - feet.Z);
                float dist = d.Length();
                if (dist < 1e-3f) continue;
                d /= dist;
                float len = MathF.Min(dist - 7, 15);
                if (len <= 2) continue;
                var colour = w.Level == StealthExposure.Detected ? PointerDetected : PointerNoticed;
                if (Project(up + d * (len * 0.35f)) is not { } a || Project(up + d * len) is not { } b) continue;
                o.Line(a.X, a.Y, b.X, b.Y, 3, colour);
                var along = b - a;
                if (along.LengthSquared() < 1) continue;
                along = Vector2.Normalize(along);
                var side = new Vector2(-along.Y, along.X) * 6;
                o.Triangle(b + along * 8, b + side, b - side, colour);
            }
        }
    }

    /// <summary>
    /// The name tag over each of the player's characters within the original's range: the <c>Kenshi_CharacterNamePanel</c> layout (<b>Verified</b>), 0.1057 x 0.0296 of the
    /// screen, the name centred in <c>Kenshi_StandardFont_Large</c> with a shadow in the middle 0.655, and while the character sneaks the <c>Stealth</c> icon on the left in
    /// the state's tag colour. The name is white and in square brackets when the character is selected, grey and bare otherwise (<c>NameTagSelected</c> / <c>NameTagUnselected</c>, the brackets from a screenshot of the original; <b>Observed</b>);
    /// In multiplayer a teammate's tag is the name in its owner's colour with the owner's name under it, and one's own tags get a 2-pixel bar in one's colour.
    /// The light level icon is not drawn (no light model). The panel stands on the head like a speech bubble does (the original's anchor is not mapped).
    /// </summary>
    void AddNameTags(List<UiControl> controls)
    {
        float width = Width * 0.105729f, height = Height * 0.0296296f, scale = Height / 1080f;
        int n = 0;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer || c.Name.Length == 0 || EditorOpen || c.CarriedBy != CharacterId.None) continue;
            var feet = where(c);
            if (Vector3.DistanceSquared(Eye, feet) >= NameTagRangeSquared) continue;
            if (Project(feet + new Vector3(0, CharacterHeight + 4, 0)) is not { } head) continue;
            float x = head.X - width / 2, y = head.Y - 4 * scale - height;
            // A teammate's character: its name in the owner's colour with the owner's name under it ("Player n" in grey when the owner left the roster).
            bool other = OtherPlayers(c);
            var owner = other ? PlayerInfo(c.Owner) : null;
            string ownerColour = other ? PlayerColours.MyGui(owner?.Colour ?? -1) : "";
            controls.Add(new($"nametag-{n}", "TextBox", new(x + width * 0.187192f, y, width * 0.655172f, height), c.Selected && !other ? $"[{c.Name}]" : c.Name, "", "", true,
                new Dictionary<string, string>
                {
                    ["FontName"] = "Kenshi_StandardFont_Large", ["TextAlign"] = "Center", ["TextShadow"] = "true",
                    ["TextColour"] = other ? ownerColour : c.Selected ? "1 1 1" : "0.702 0.702 0.702",
                }, Vector4.Zero, Pickable: false));
            if (other)
                controls.Add(new($"nametag-owner-{n}", "TextBox", new(x + width * 0.187192f, y + height, width * 0.655172f, height * 0.7f), owner?.Name ?? $"Player {c.Owner}", "", "", true,
                    new Dictionary<string, string>
                    {
                        ["FontName"] = "Kenshi_StandardFont_MediumSmall", ["TextAlign"] = "Center", ["TextShadow"] = "true", ["TextColour"] = ownerColour, ["Alpha"] = "0.85",
                    }, Vector4.Zero, Pickable: false));
            else if (Multiplayer && PlayerInfo(LocalPlayer) is { } me)
                controls.Add(new($"nametag-mine-{n}", "Widget", new(head.X - width * 0.2f, y + height + scale, width * 0.4f, 2 * scale), "", "", "", true,
                    new Dictionary<string, string>(), PlayerColours.Of(me.Colour), Pickable: false));
            if (c.Stealth is { } state)
            {
                var tint = state switch { StealthExposure.Detected => TagSeen, StealthExposure.Noticed => TagAlmostSeen, _ => TagHidden };
                controls.Add(new($"nametag-stealth-{n}", "ImageBox", new(x, y + height * 0.09375f, width * 0.187192f, height * 0.84375f), "", "", "", true,
                    new Dictionary<string, string>
                    {
                        ["ImageResource"] = "Kenshi_CharacterNameTags", ["ImageGroup"] = "Stealth", ["ImageName"] = "Stealth",
                        ["Colour"] = FormattableString.Invariant($"{tint.X} {tint.Y} {tint.Z} 1"),
                    }, default, Pickable: false));
            }
            n++;
        }
    }
}
