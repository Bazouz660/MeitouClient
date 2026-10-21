using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Meitou.Game;

/// <summary>The unit the run speed is shown in.</summary>
enum SpeedUnit { Mph, Kmh }

/// <summary>
/// The player's settings, kept in <c>meitou.user.json</c> (git-ignored) in the working directory, else next to the executable:
/// the frame limit, vsync, full screen, the simulation tick rate, the graphics sliders of the Tab panel (by label) and key bindings
/// (action name to comma-separated keys, as <see cref="Engine.Input.InputBindings.Apply"/> reads them). Written back on exit.
/// One file is shared by every installed version (the launcher's data folder) and edited by the launcher too: docs/launcher.md "Graphics".
/// </summary>
sealed class UserConfig
{
    public const string FileName = "meitou.user.json";

    /// <summary>
    /// The layout of this file, its <c>version</c> key; a file without one (written before it existed) is 1. Raise it when a key is renamed,
    /// retyped or read with a new meaning, and convert older files in <see cref="Migrate"/>. A Tab panel slider whose numbering changes
    /// takes a new label instead (<c>WorldFrame.UpscalerSliders</c>), so its old value is not misread.
    /// </summary>
    public const int CurrentVersion = 1;
    /// <summary>The file's layout: <see cref="CurrentVersion"/>, or the newer one of a later release whose file this is (kept, so that
    /// release does not convert its own keys again).</summary>
    public int Version { get; set; } = CurrentVersion;
    /// <summary>Top-level keys this release does not know (a newer one's), written back as read.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Frames per second when vsync is off; 0 = unlimited. Engine default 150.</summary>
    public int FpsLimit { get; set; } = 150;
    public bool VSync { get; set; }
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
    public bool CharacterFlights { get; set; } = false;
    /// <summary>Keeps the object meshes and textures and the terrain textures around every squad bar character loaded, not only around the camera (Advanced settings; Meitou only, costs video memory).</summary>
    public bool KeepTexturesAtCharacters { get; set; } = true;
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
    /// <summary>Campaign data counts each <c>inherits from</c> ancestor once (Fixes settings; Faithful off: twice, as the game does, doubling inherited squad weights and follow-up chances).</summary>
    public bool CampaignAncestorsOnce { get; set; } = true;
    /// <summary>Wild animals and homeless squads keep their characters while their area is unloaded (Advanced settings; Faithful off: removed and made anew, as the game does).</summary>
    public bool KeepWild { get; set; } = true;
    /// <summary>Town residents back after an hour or more unloaded are where their daily schedule puts them (Advanced settings; Faithful off: where they stood, as the game does).</summary>
    public bool ResidentSchedule { get; set; } = true;
    /// <summary>Doors a walker opened close behind it after 5 s (Advanced settings; Faithful off: they stay open, as in the game; docs/simulation.md "Cages, beds and locks").</summary>
    public bool DoorsCloseBehind { get; set; } = true;
    /// <summary>Fixes of the game's own bugs (Fixes settings; on: fixed, off: as the game does; docs/audio.md): doors and gates sound when they break,
    /// a biome entered before its bank loads gets the right day or night ambience, and the wind bed stops when the wind does.</summary>
    public bool FixDoorBreak { get; set; } = true;
    public bool FixAmbienceSuffix { get; set; } = true;
    public bool FixWindStop { get; set; } = true;
    /// <summary>The strategy camera's turn and tilt rate in degrees per second per unit of input (Controls settings): the original's
    /// <c>mouse speed pan</c> / <c>mouse speed tilt</c> (slider 2 … 50, default 6), one value for both here.</summary>
    public float CameraRotateSpeed { get; set; } = Meitou.Data.World.KenshiCamera.DefaultRotateSpeed;
    public const float MinCameraRotateSpeed = 2, MaxCameraRotateSpeed = 50;
    /// <summary>The Windows cursor instead of the game's drawn pointer (the original's <c>hardware_mouse</c>, default off; Interface settings).</summary>
    public bool HardwareMouse { get; set; }
    /// <summary>Outline and tint the character under the cursor (Advanced settings; Meitou only, the game has none).</summary>
    public bool HoverOutline { get; set; } = true;
    /// <summary>The green ring under each selected character (Advanced settings; default on).</summary>
    public bool SelectionRings { get; set; } = true;
    /// <summary>A character that takes a fatal hit explodes, covers everything around in blood and sends bones flying (Advanced settings; Meitou only, off by default; docs/game/luckyren.md).</summary>
    public bool LuckyrenMode { get; set; }
    /// <summary>How many items dropped in play stay on the ground before the oldest goes (Advanced settings; <see cref="Meitou.Simulation.Items.GroundItems.Limit"/>).</summary>
    public int GroundItemLimit { get; set; } = Meitou.Simulation.Items.GroundItems.DefaultLimit;
    /// <summary>The audio sliders (Audio settings): the original's six volumes and the music frequency; null until first used, then taken from the install's settings.cfg.</summary>
    public Meitou.Audio.AudioSettings? Audio { get; set; }
    /// <summary>Worker threads of the simulation; null = half the cores.</summary>
    public int? SimThreads { get; set; }
    public Dictionary<string, float> Graphics { get; set; } = [];
    public Dictionary<string, string> Bindings { get; set; } = [];

    // Multiplayer (docs/meitou-ui.md "Multiplayer"): new keys only, so no version bump. Passwords are never stored.
    /// <summary>Your name in multiplayer; null until the Multiplayer window saves one.</summary>
    public string? PlayerName { get; set; }
    /// <summary>Your identity to hosts, which keeps your player number and squads across sessions and saves; <see cref="Guid.Empty"/> until <see cref="EnsurePlayerId"/>.
    /// A second instance on one machine needs its own file (<c>--config</c>, <see cref="LoadOrCreate"/>): a host refuses its own id and drops the older connection of a repeated one.</summary>
    public Guid PlayerId { get; set; }
    /// <summary>The UDP port a hosted game listens on.</summary>
    public int HostPort { get; set; } = MultiplayerDefaults.Port;
    /// <summary>Players in a hosted game, the host included (2 to <see cref="MultiplayerDefaults.MaxPlayersLimit"/>).</summary>
    public int HostMaxPlayers { get; set; } = MultiplayerDefaults.MaxPlayers;
    /// <summary>Whether joined players may pause and change the speed of a hosted game.</summary>
    public bool HostClientsControlTime { get; set; } = true;
    /// <summary>Whether joined players may use the F10 debug menu in a hosted game.</summary>
    public bool HostAllowCheats { get; set; }
    /// <summary>Hosting and joining through Steam as Kenshi (app 233860; docs/net.md "Steam transport"); false keeps Meitou off Steam (UDP only).</summary>
    public bool Steam { get; set; } = true;
    /// <summary>The games joined, newest first, at most <see cref="MaxRecentGames"/>, unique by <see cref="RecentGame.Address"/> (ignoring case); see <see cref="RememberGame"/>.</summary>
    public List<RecentGame> RecentGames { get; set; } = [];
    public const int MaxRecentGames = 8;

    /// <summary>The <see cref="PlayerId"/>, made on first use (the caller saves).</summary>
    public Guid EnsurePlayerId()
    {
        if (PlayerId == Guid.Empty) PlayerId = Guid.NewGuid();
        return PlayerId;
    }

    /// <summary>Puts a joined game first in <see cref="RecentGames"/>, dropping an older entry of the same address and any past <see cref="MaxRecentGames"/> (the caller saves).</summary>
    public void RememberGame(string address, string host, DateTime joinedUtc)
    {
        RecentGames.RemoveAll(g => string.Equals(g.Address, address, StringComparison.OrdinalIgnoreCase));
        RecentGames.Insert(0, new RecentGame { Address = address, Host = host, LastJoinedUtc = joinedUtc });
        if (RecentGames.Count > MaxRecentGames) RecentGames.RemoveRange(MaxRecentGames, RecentGames.Count - MaxRecentGames);
    }

    [JsonIgnore]
    public string Path { get; private set; } = FileName;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static UserConfig Load()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var file = System.IO.Path.Combine(dir, FileName);
            if (File.Exists(file)) return Load(file);
        }
        return new UserConfig { Path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), FileName) };
    }

    /// <summary>Reads an existing <paramref name="file"/> (see <see cref="Read"/>); <see cref="Save"/> writes back to it.</summary>
    internal static UserConfig Load(string file)
    {
        var config = Read(file) ?? new UserConfig();
        config.Path = file;
        return config;
    }

    /// <summary>A settings file named on the command line (<c>--config</c>, a second instance on one machine): <see cref="Load(string)"/> when it exists, else the defaults, which <see cref="Save"/> writes there.</summary>
    public static UserConfig LoadOrCreate(string file) =>
        File.Exists(file) ? Load(file) : new UserConfig { Path = System.IO.Path.GetFullPath(file) };

    /// <summary>
    /// Reads the file key by key: a value this release cannot read (a newer release changed its type, a hand edit) is dropped and takes its
    /// default, the rest are kept. Before anything is dropped the file is copied aside, as the next save writes over it.
    /// </summary>
    static UserConfig? Read(string file)
    {
        JsonObject? json;
        try { json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"config    {file} is not valid ({e.Message}); using the defaults");
            Backup(file);
            return null;
        }
        if (json is null)
        {
            Console.Error.WriteLine($"config    {file} is not a JSON object; using the defaults");
            Backup(file);
            return null;
        }
        int version = json["version"] is JsonValue v && v.TryGetValue(out int n) ? n : 1;
        if (version < CurrentVersion) Migrate(json, version);
        else if (version > CurrentVersion) Console.WriteLine($"config    {file} is from a newer Meitou (layout {version}, this one reads {CurrentVersion}); its new keys are kept");
        json["version"] = Math.Max(version, CurrentVersion);
        bool backedUp = false;
        for (int attempt = 0; ; attempt++)
        {
            try { return json.Deserialize<UserConfig>(Json); }
            catch (JsonException e) when (attempt < 100)
            {
                backedUp = backedUp || Backup(file);
                if (!RemoveAt(json, e.Path))
                {
                    Console.Error.WriteLine($"config    {file} is not valid ({e.Message}); using the defaults");
                    return null;
                }
                Console.Error.WriteLine($"config    {file}: {e.Path} could not be read ({e.Message}); using its default");
            }
        }
    }

    /// <summary>Converts a file of an older <see cref="CurrentVersion"/>, one step at a time. A renamed key moves only where the new one is
    /// missing: the launcher may already have written it.</summary>
    static void Migrate(JsonObject json, int from)
    {
        // No steps yet: 1 is the first layout. A raise to 2 adds `if (from < 2) { ... }`, and so on.
        _ = (json, from);
    }

    /// <summary>Copies the file to <c>meitou.user.&lt;time&gt;.bak.json</c> beside it.</summary>
    static bool Backup(string file)
    {
        var copy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? ".", $"meitou.user.{DateTime.Now:yyyyMMdd-HHmmss}.bak.json");
        try
        {
            File.Copy(file, copy, overwrite: true);
            Console.Error.WriteLine($"config    kept a copy of the old file as {copy}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"config    could not copy {file}: {e.Message}");
            return false;
        }
    }

    /// <summary>Removes the value a <see cref="JsonException.Path"/> names (<c>$.a.b</c>, <c>$['a b']</c>, <c>$.a[2]</c>); false when there is none.</summary>
    static bool RemoveAt(JsonObject json, string? path)
    {
        if (path is null || !path.StartsWith('$')) return false;
        var segments = new List<object>();
        for (int i = 1; i < path.Length;)
        {
            if (path[i] == '.')
            {
                int end = path.IndexOfAny(['.', '['], i + 1);
                if (end < 0) end = path.Length;
                segments.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path.AsSpan(i).StartsWith("['"))
            {
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                segments.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), out int index)) return false;
                segments.Add(index);
                i = end + 1;
            }
            else return false;
        }
        if (segments.Count == 0) return false;
        JsonNode? parent = json;
        foreach (var segment in segments.SkipLast(1))
            parent = segment is int index ? parent is JsonArray a && index < a.Count ? a[index] : null : parent is JsonObject o ? o[(string)segment] : null;
        switch (parent, segments[^1])
        {
            case (JsonObject o, string key): return o.Remove(key);
            case (JsonArray a, int index) when index < a.Count: a.RemoveAt(index); return true;
            default: return false;
        }
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Json)); }
        catch (IOException e) { Console.Error.WriteLine($"config    could not write {Path}: {e.Message}"); }
        catch (UnauthorizedAccessException e) { Console.Error.WriteLine($"config    could not write {Path}: {e.Message}"); }
    }
}

/// <summary>A game joined (<see cref="UserConfig.RecentGames"/>): the address as typed (<see cref="JoinAddress"/> reads it), the host's name then, and when.</summary>
sealed class RecentGame
{
    public string Address { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime LastJoinedUtc { get; set; }
}
