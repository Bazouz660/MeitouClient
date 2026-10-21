using System.Numerics;
using Meitou.Data.Particles;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Rendering;
using EffectType = Meitou.Data.Particles.EffectType;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;

namespace Meitou.Game;

sealed partial class GameHost
{
    TitleComposition? titleComposition;
    TitleBattle? titleBattle;
    Meitou.Data.Characters.CharacterAppearance[] titleAppearances = [];
    bool TitleCharactersReady => ActiveCharacters is not { } characters ||
        titleAppearances.Length > 0 && titleAppearances.All(characters.IsPrepared) && !characters.ContentPending;
    bool TitleSceneReady => TitleCharactersReady && (gpu is null || gpu.Streamer is not { Idle: false } &&
        !(gpu.Objects?.Pending > 0) && !(gpu.Foliage?.Pending > 0));
    Func<ParticleRenderer>? titleParticleFactory;
    ParticleRenderer? titleParticles;
    IReadOnlyList<WeatherEffectEntry>? titleDust;
    float? gameplayLens;
    /// <summary>The gameplay character source, set aside while the title shows its staged actors.</summary>
    Action<CharacterDrawList>? gameplayCharacters;
    bool TitleVisible => player.TitleOpen && player.UiMode == UiMode.Meitou;
    float TitleHour => scene.Clock.Sunrise + 0.75f;
    /// <summary>The moons' fill light, and the shafts' air layer (a multiple of the usual) on the title: thick morning air so the low sun casts strong rays.</summary>
    const float TitlePlanetFill = 0.8f, TitleShaftAir = 2, TitleClouds = 0.38f;
    /// <summary>The title's white balance: keeps the orange of the early hour with the sun a little higher than that hour would put it.</summary>
    static readonly System.Numerics.Vector3 TitleBalance = new(1.1f, 0.98f, 0.88f);
    /// <summary>The wanderer's fill light on the title: warm sky light so his back, turned to the low sun, is not a flat silhouette.</summary>
    static readonly System.Numerics.Vector3 TitleFill = new(0.064f, 0.052f, 0.04f);

    void EnsureTitleScene()
    {
        if (titleComposition is not null || scene.Database is not { } db) return;
        titleComposition = new(install, db, scene.GroundAt, SandboxRecord(db));
        if (popData is { } data && animationLengths is { } lengths && animationLibrary is { } library)
            titleBattle = TitleBattle.Create(data, scene.GroundAt, titleComposition.Fights, lengths, library);
        if (ActiveCharacters is { } characters)
        {
            var list = new CharacterDrawList();
            FillTitleCharacters(list);
            titleAppearances = [.. list.Items.Select(c => c.Appearance)];
            foreach (var appearance in titleAppearances) characters.Prefetch(appearance);
        }
    }

    void FillTitleCharacters(CharacterDrawList list)
    {
        titleComposition?.Fill(list);
        titleBattle?.Fill(list);
    }

    /// <summary>
    /// The title's own view of the real world (<see cref="TitleComposition.Spot"/>), independent of the gameplay camera rig; the world's renderers
    /// stream round it as round any camera. While the title shows, only its staged actors are drawn.
    /// </summary>
    void ApplyTitleCamera()
    {
        if (!TitleVisible)
        {
            if (gameplayLens is { } lens) { camera.FieldOfView = lens; camera.MaxNear = 200; gameplayLens = null; }
            if (gameplayCharacters is { } source && ActiveCharacters is { } restore) { restore.Source = source; gameplayCharacters = null; }
            if (ActiveFoliage is { } plants && titleComposition is { } shown && plants.Shift == shown.Ruins) plants.Shift = null;
            if (ActiveObjects is { } things && titleComposition is { } staged && things.Staged == staged.Staged) things.Staged = null;
            return;
        }
        EnsureTitleScene();
        if (titleComposition is not { } title) return;
        gameplayLens ??= camera.FieldOfView;
        player.TitleLogoSource ??= TitleLogo.Load;
        if (ActiveCharacters is { } characters && gameplayCharacters is null)
        {
            gameplayCharacters = characters.Source;
            characters.Source = FillTitleCharacters;
        }
        if (ActiveFoliage is { } foliage && foliage.Shift != title.Ruins) foliage.Shift = title.Ruins;
        if (ActiveObjects is { } objects && objects.Staged != title.Staged) objects.Staged = title.Staged;
        TitleComposition.Place(camera);
    }

    void DrawTitleWorld(int width, int height)
    {
        EnsureTitleScene();
        if (titleComposition is not { } title) return;
        double seconds = realTime.Elapsed.TotalSeconds;
        PrepareTitleDust(seconds);
        // Clear sky with a few clouds whatever the region's weather, no placed fog volumes, the sun turned into the right of the view, the moons lit
        // full face so they read by day, and thicker morning air so the hill the sun peeks over casts rays through it.
        var sky = gpu.Sky;
        var (weather, coverage, regions, clock, wind) = (sky.Weather, sky.CloudCoverage, gpu.Weather, scene.Clock, sky.CloudWind);
        float hour = presentationHour ?? TitleHour;
        // Fade the staged fill with the sun, reaching zero at the horizon.
        float fill = Math.Clamp(clock.SunDirection(hour).Y / MathF.Max(clock.SunDirection(TitleHour).Y, 1e-3f), 0, 1);
        fill = fill * fill * (3 - 2 * fill);
        sky.Weather = SkyWeather.Default;
        sky.CloudCoverage = TitleClouds;
        sky.CloudWind = TitleComposition.CloudWind(camera);
        sky.PlanetTurn = TitleComposition.MoonTurn(camera);
        sky.PlanetFill = TitlePlanetFill * fill;
        if (gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = true;
        if (gpu.Characters is { } lit) lit.Fill = TitleFill * fill;
        var shafts = gpu.Post?.Options;
        var (air, strength, balance) = shafts is null ? (0f, 0f, System.Numerics.Vector3.One) : (shafts.ShaftAir, shafts.ShaftStrength, shafts.Balance);
        if (shafts is not null) (shafts.ShaftAir, shafts.ShaftStrength, shafts.Balance) = (TitleShaftAir, 1, TitleBalance);
        gpu.Weather = null;
        scene.Clock = clock with { SunTurn = TitleComposition.SunTurn(clock, hour, camera) };
        // A separate particle scene keeps the menu's dust out of gameplay and excludes the map's effect placers.
        var worldParticles = gpu.Particles;
        float density = gpu.ParticleDensity;
        bool weatherParticles = gpu.WeatherParticles;
        gpu.Particles = titleParticles;
        gpu.ParticleDensity = 0.3f;
        gpu.WeatherParticles = true;
        if (gpu.Foliage is { } foliage && o.Screenshot is null) foliage.SwaySeconds = seconds;
        try { WorldFrame.Draw(gpu, scene, camera, render, width, height, hour, (float)seconds / 600f, o.FogDistance); }
        finally
        {
            (gpu.Particles, gpu.ParticleDensity, gpu.WeatherParticles) = (worldParticles, density, weatherParticles);
            (sky.Weather, sky.CloudCoverage, gpu.Weather, scene.Clock, sky.PlanetFill, sky.CloudWind, sky.PlanetTurn) = (weather, coverage, regions, clock, 0, wind, System.Numerics.Quaternion.Identity);
            if (shafts is not null) (shafts.ShaftAir, shafts.ShaftStrength, shafts.Balance) = (air, strength, balance);
            if (gpu.FogVolumes is { } restoreFog) restoreFog.HidePlaced = false;
            if (gpu.Characters is { } unlit) unlit.Fill = default;
        }
    }

    void PrepareTitleDust(double seconds)
    {
        if (titleParticles is null && titleParticleFactory is { } create)
        {
            titleParticles = create();
            titleParticles.PrewarmSeconds = 5;
            titleParticles.Seed = 173;
            titleParticles.World.Density = 0.3f;
            titleDust = titleParticles.Library.FindSystem("kenshi_weather_dust1") is null ? [] :
                [new(new EffectRecord("Title dust", EffectType.Camera, "kenshi_weather_dust1", true, false, 1,
                    0, 0, Vector3.One, 0, false, 4000, 0, 0, 0, 0), 1, 0, 0)];
        }
        if (titleParticles is not { } dust) return;
        var left = -Vector3.Normalize(Vector3.Cross(camera.Forward, Vector3.UnitY));
        float gust = 0.5f + 0.5f * MathF.Sin((float)(seconds % 120) * MathF.Tau / 24);
        dust.SetWeather(new WeatherEffectInput { Effects = titleDust!, Wind = new Vector2(left.X, left.Z) * (35 + 20 * gust) });
        // Warm once before setting the tint; Draw advances subsequent frames on the menu's real-time clock.
        if (dust.Groups.Count == 0) dust.Update(seconds, camera);
        float fade = Math.Clamp((camera.Pitch + 0.65f) / 0.45f, 0, 1);
        foreach (var group in dust.Groups)
            foreach (var unit in group.Units)
                unit.Tint = new Vector3(1, 0.86f, 0.68f) * ((0.4f + 0.25f * gust) * fade);
    }

    void DisposeTitleScene()
    {
        titleParticles?.Dispose();
        titleParticles = null;
        titleBattle?.Dispose();
        titleBattle = null;
        titleAppearances = [];
        titleComposition = null;
    }
}
