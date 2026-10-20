using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>The recipe kinds of a <c>LookDefinition</c> (docs/net.md, "Looks"); append-only within a protocol version.</summary>
public enum RecipeKind : byte
{
    /// <summary><see cref="AppearanceRecipe.Rolled"/>: <c>Create(recordId, factionId, seed)</c>.</summary>
    Rolled = 1,
    /// <summary><see cref="AppearanceRecipe.Kit"/>: <c>Create(recordId, loadout)</c>.</summary>
    Kit = 2,
}

/// <summary>
/// How a client makes a look again: the arguments of the <see cref="IAppearanceSource"/> call that made it on the host (docs/net.md, "Looks"). A client's own
/// <see cref="GeneratedAppearances"/> over the same data repeats the call and gets the same parts, so a look crosses the wire as a few ids, never as meshes.
/// <see cref="Shape"/> is the look's fixed body shape when one was set on it (<see cref="CharacterAppearance.Shape"/>, the staged title character); null in play.
/// </summary>
public abstract record AppearanceRecipe
{
    /// <summary>The fixed body shape to set on the rebuilt look; null: none (the renderer computes it, <see cref="CharacterShape.InputFor"/>).</summary>
    public CharacterShapeInput? Shape { get; init; }

    /// <summary>The wire kind.</summary>
    public abstract RecipeKind Kind { get; }

    /// <summary>A look rolled from a seed: a CHARACTER (or RACE) record, the faction that limits hair and beards, the seed. Animals too (seed and faction unused).</summary>
    public sealed record Rolled(string RecordId, string? FactionId, int Seed) : AppearanceRecipe
    {
        public override RecipeKind Kind => RecipeKind.Rolled;
    }

    /// <summary>A look drawn from a full loadout (a saved, edited or re-equipped character: its CHARACTER_APPEARANCE, worn items, weapons and limbs).</summary>
    public sealed record Kit(string RecordId, LoadoutWire Loadout) : AppearanceRecipe
    {
        public override RecipeKind Kind => RecipeKind.Kit;
    }

    /// <summary>
    /// The recipe of a look the recorder never saw (made before it was installed, or not through it): an animal's species look, else a Kit from the look's own
    /// <see cref="CharacterAppearance.Loadout"/> (every person <see cref="GeneratedAppearances"/> makes has one). Null for a look with neither, which a client then
    /// shows as a marker.
    /// </summary>
    public static AppearanceRecipe? FromLook(CharacterAppearance look)
    {
        AppearanceRecipe? recipe = look.Animal is { } animal ? new Rolled(animal.StringId, null, 0)
            : look.Loadout is { } loadout ? new Kit(look.Character?.StringId ?? loadout.Character?.StringId ?? look.Race.StringId, LoadoutWire.From(loadout))
            : null;
        return recipe is null || look.Shape is null ? recipe : recipe with { Shape = look.Shape };
    }

    /// <summary>
    /// Makes the look again through <paramref name="source"/>. Null when the source gives none or the client's data lacks a record the recipe names (a marker is
    /// shown instead). A shape is set only on a person's look: an animal's look is shared by its species.
    /// </summary>
    public CharacterAppearance? Build(IAppearanceSource source, GameDatabase db)
    {
        var look = this switch
        {
            Rolled r => source.Create(r.RecordId, r.FactionId, r.Seed),
            Kit k => k.Loadout.ToLoadout(db) is { } loadout ? source.Create(k.RecordId, loadout) : null,
            _ => null,
        };
        if (look is not null && Shape is not null && look.Animal is null) look.Shape = Shape;
        return look;
    }
}

/// <summary>A <see cref="LoadoutItem"/> with its records as string ids.</summary>
public sealed record LoadoutItemWire(string Record, int Quality, string? Material);

/// <summary>A <see cref="LoadoutWeapon"/> with its records as string ids.</summary>
public sealed record LoadoutWeaponWire(string Weapon, string? Manufacturer, string? Model, int Level, string Section, int Row);

/// <summary>A <see cref="LoadoutLimb"/> (1 stump, 2 replaced, 3 crushed) with its prosthetic as a string id.</summary>
public sealed record LoadoutLimbWire(int State, string? Prosthetic);

/// <summary>
/// A <see cref="Loadout"/> as it crosses the wire (docs/net.md, "Looks"): every record is a string id, the appearance is the whole CHARACTER_APPEARANCE record,
/// <see cref="Loadout.Notes"/> are dropped. The lists are copied when the look is made, so a later change to the host's loadout lists cannot change a recipe;
/// the CHARACTER_APPEARANCE record is shared (no code edits one in place: an edit makes a new record).
/// </summary>
public sealed record LoadoutWire(
    int Seed, string? Character, string Race, bool Female, FcsRecord Appearance, bool Generated, int MorphIndex,
    IReadOnlyList<LoadoutItemWire> Clothing, LoadoutItemWire? Backpack, IReadOnlyList<LoadoutItemWire> Carried,
    LoadoutWeaponWire? Crossbow, IReadOnlyList<LoadoutWeaponWire> Weapons, IReadOnlyList<LoadoutLimbWire?> Limbs)
{
    /// <summary>Limb slots (0 left arm, 1 right arm, 2 left leg, 3 right leg), as <see cref="Loadout.Limbs"/>.</summary>
    public const int LimbCount = 4;

    public static LoadoutWire From(Loadout loadout) => new(
        loadout.Seed, loadout.Character?.StringId, loadout.Race.StringId, loadout.Female, loadout.Appearance.Record, loadout.Generated, loadout.MorphIndex,
        [.. loadout.Clothing.Select(ItemOf)], loadout.Backpack is { } pack ? ItemOf(pack) : null, [.. loadout.Carried.Select(ItemOf)],
        loadout.Crossbow is { } bow ? WeaponOf(bow) : null, [.. loadout.Weapons.Select(WeaponOf)],
        [.. Enumerable.Range(0, LimbCount).Select(k => k < loadout.Limbs.Length && loadout.Limbs[k] is { } l ? new LoadoutLimbWire(l.State, l.Prosthetic?.StringId) : null)]);

    /// <summary>The loadout over <paramref name="db"/>'s records; null when any record it names is missing there.</summary>
    public Loadout? ToLoadout(GameDatabase db)
    {
        bool missing = false;
        GameRecord? Find(string? id)
        {
            if (id is null) return null;
            var record = db.Find(id);
            missing |= record is null;
            return record;
        }
        LoadoutItem? Item(LoadoutItemWire? i) => i is null || Find(i.Record) is not { } r ? null : new LoadoutItem(r) { Quality = i.Quality, Material = Find(i.Material) };
        LoadoutWeapon? Weapon(LoadoutWeaponWire? w) => w is null || Find(w.Weapon) is not { } r ? null
            : new LoadoutWeapon(r) { Manufacturer = Find(w.Manufacturer), Model = Find(w.Model), Level = w.Level, Section = w.Section, Row = w.Row };

        var character = Find(Character);
        if (Find(Race) is not { } race) return null;
        var loadout = new Loadout
        {
            Seed = Seed, Character = character, Race = race, Female = Female, Appearance = new AppearanceFile { Record = Appearance },
            Generated = Generated, MorphIndex = MorphIndex,
            Backpack = Item(Backpack), Crossbow = Weapon(Crossbow),
        };
        foreach (var c in Clothing) if (Item(c) is { } item) loadout.Clothing.Add(item);
        foreach (var c in Carried) if (Item(c) is { } item) loadout.Carried.Add(item);
        foreach (var w in Weapons) if (Weapon(w) is { } weapon) loadout.Weapons.Add(weapon);
        for (int k = 0; k < Math.Min(Limbs.Count, loadout.Limbs.Length); k++)
            if (Limbs[k] is { } limb) loadout.Limbs[k] = new LoadoutLimb(limb.State, Find(limb.Prosthetic));
        return missing ? null : loadout;
    }

    static LoadoutItemWire ItemOf(LoadoutItem i) => new(i.Record.StringId, i.Quality, i.Material?.StringId);

    static LoadoutWeaponWire WeaponOf(LoadoutWeapon w) => new(w.Weapon.StringId, w.Manufacturer?.StringId, w.Model?.StringId, w.Level, w.Section, w.Row);
}
