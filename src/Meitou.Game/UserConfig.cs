using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Game;

/// <summary>The unit the run speed is shown in.</summary>
enum SpeedUnit { Mph, Kmh }

/// <summary>The Metal display path: native at full size, MetalFX temporal (quality/balanced), MetalFX spatial (performance), or the experimental TAA.</summary>
enum MetalUpscalerMode { Native, Temporal, Spatial, Taa }

/// <summary>
/// The player's settings, kept in <c>meitou.user.json</c> (git-ignored) in the working directory, else next to the executable:
/// the frame limit, vsync, full screen, the simulation tick rate, the graphics sliders of the Tab panel (by label) and key bindings
/// (action name to comma-separated keys, as <see cref="Engine.Input.InputBindings.Apply"/> reads them). Written back on exit.
/// </summary>
sealed class UserConfig
{
    public const string FileName = "meitou.user.json";

    /// <summary>Frames per second when vsync is off; 0 = unlimited. Engine default 150.</summary>
    public int FpsLimit { get; set; } = 150;
    public bool VSync { get; set; }
    /// <summary>The Metal display default: MetalFX balanced (temporal at 0.50); Native is used when MetalFX is unavailable.</summary>
    public MetalUpscalerMode MetalUpscaler { get; set; } = MetalUpscalerMode.Temporal;
    /// <summary>The Metal render scale; the default 0.50 is the balanced profile. The Metal settings panel edits it live.</summary>
    public float MetalRenderScale { get; set; } = 0.5f;
    public Meitou.Data.Ui.UiMode UiMode { get; set; } = Meitou.Data.Ui.UiMode.Meitou;
    /// <summary>The unit of the run speed in the information panel (Interface settings): mph as the game, or km/h.</summary>
    public SpeedUnit SpeedUnit { get; set; } = SpeedUnit.Mph;
    /// <summary>The damage floaters (Interface settings): the original's <c>floaters</c> option, Simple by default; Meitou also floats a green "+N" over a player character that heals.</summary>
    public FloaterMode Floaters { get; set; } = FloaterMode.Simple;
    public bool MainMenuTransitions { get; set; } = true;
    public bool InGameTransitions { get; set; } = true;
    public float MainMenuTransitionSpeed { get; set; } = 1.5f;
    public float InGameTransitionSpeed { get; set; } = 1.5f;
    /// <summary>A double click (or double tap) on a squad member farther than <see cref="FlightDistance"/> from the view flies the camera there in an arc (Advanced settings; Meitou only, the game cuts).</summary>
    public bool CharacterFlights { get; set; } = true;
    /// <summary>Seconds a character flight (and each squad tour hop, numpad /) takes.</summary>
    public float CharacterFlightSeconds { get; set; } = DefaultFlightSeconds;
    public const float DefaultFlightSeconds = 2, FlightDistance = 10000;
    /// <summary>Borderless full screen (the Tab panel, Alt+Enter); <c>--windowed</c> starts in a window for one run.</summary>
    public bool FullScreen { get; set; } = true;
    public int TickRate { get; set; } = 30;
    /// <summary>How many enemies may melee one target at once (Advanced settings, 1-5): Faithful 1 (the game's CONSTANTS), Meitou 3.</summary>
    public int AttackSlots { get; set; } = MeitouAttackSlots;
    public const int FaithfulAttackSlots = 1, MeitouAttackSlots = 3;
    /// <summary>A late but successfully rolled dodge or block still counts (Advanced settings; Faithful off, Meitou on): a balance change for fights against several enemies.</summary>
    public bool LateDefences { get; set; } = true;
    /// <summary>The off-screen switches (Advanced settings; Faithful off, Meitou on; docs/simulation.md "Off-screen tier"): roaming squads keep their characters off screen,
    /// hostile squads that meet off screen fight it out, a squad coming back has healed for the time away.</summary>
    public bool KeepRoamers { get; set; } = true;
    public bool OffscreenFights { get; set; } = true;
    public bool OffscreenHealing { get; set; } = true;
    /// <summary>Campaign data counts each <c>inherits from</c> ancestor once (Advanced settings; Faithful off: twice, as the game does, doubling inherited squad weights and follow-up chances).</summary>
    public bool CampaignAncestorsOnce { get; set; } = true;
    /// <summary>Wild animals and homeless squads keep their characters while their area is unloaded (Advanced settings; Faithful off: removed and made anew, as the game does).</summary>
    public bool KeepWild { get; set; } = true;
    /// <summary>Doors a walker opened close behind it after 5 s (Advanced settings; Faithful off: they stay open, as in the game; docs/simulation.md "Cages, beds and locks").</summary>
    public bool DoorsCloseBehind { get; set; } = true;
    /// <summary>The Windows cursor instead of the game's drawn pointer (the original's <c>hardware_mouse</c>, default off; Interface settings).</summary>
    public bool HardwareMouse { get; set; }
    /// <summary>Outline and tint the character under the cursor (Advanced settings; Meitou only, the game has none).</summary>
    public bool HoverOutline { get; set; } = true;
    /// <summary>A character that takes a fatal hit explodes, covers everything around in blood and sends bones flying (Advanced settings; Meitou only, off by default). Stored only: nothing reads it yet.</summary>
    public bool LuckyrenMode { get; set; }
    /// <summary>The audio sliders (Audio settings): the original's six volumes and the music frequency; null until first used, then taken from the install's settings.cfg.</summary>
    public Meitou.Audio.AudioSettings? Audio { get; set; }
    /// <summary>Worker threads of the simulation; null = half the cores.</summary>
    public int? SimThreads { get; set; }
    public Dictionary<string, float> Graphics { get; set; } = [];
    public Dictionary<string, string> Bindings { get; set; } = [];

    [JsonIgnore]
    public string Path { get; private set; } = FileName;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static UserConfig Load()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var file = System.IO.Path.Combine(dir, FileName);
            if (!File.Exists(file)) continue;
            try
            {
                var config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(file), Json) ?? new UserConfig();
                config.Path = file;
                config.Normalize();
                return config;
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine($"config    {file} is not valid ({e.Message}); using the defaults");
                return new UserConfig { Path = file };
            }
        }
        return new UserConfig { Path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), FileName) };
    }

    /// <summary>Keeps a hand-edited or older config inside the values the Metal path can use.</summary>
    void Normalize()
    {
        if (!float.IsFinite(MetalRenderScale)) MetalRenderScale = 0.5f;
        MetalRenderScale = Math.Clamp(MetalRenderScale, 0.33f, 1f);
        if (!Enum.IsDefined(MetalUpscaler)) MetalUpscaler = MetalUpscalerMode.Temporal;
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Json)); }
        catch (IOException e) { Console.Error.WriteLine($"config    could not write {Path}: {e.Message}"); }
        catch (UnauthorizedAccessException e) { Console.Error.WriteLine($"config    could not write {Path}: {e.Message}"); }
    }
}
