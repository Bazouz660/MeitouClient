using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Game;
using static Meitou.Tests.Save.SaveSamples;

namespace Meitou.Tests.Gameplay;

public class NewGameSettingsTests
{
    [Fact]
    public void SaveRoundTripKeepsAllOptionsAndOldSavesUseNeutralValues()
    {
        var options = new NewGameSettings();
        float[] values = [3.25f, 2, 1.5f, .75f, 1.25f, 2, 3.5f];
        for (int i = 0; i < values.Length; i++) options = options.Set(i, values[i]);
        var game = new SaveBuilder(17).Game;
        options.Write(game.Camera);
        string folder = Temp("difficulty");
        try
        {
            game.Write(folder);
            var loaded = NewGameSettings.Read(SaveGame.Load(folder).Camera);
            for (int i = 0; i < values.Length; i++) Assert.Equal(values[i], loaded[i]);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        var old = NewGameSettings.Read(new SaveCamera(new FcsRecord()));
        for (int i = 0; i < values.Length; i++) Assert.Equal(1, old[i]);
    }
}
