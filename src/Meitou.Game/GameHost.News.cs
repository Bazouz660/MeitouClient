using Meitou.Net.Events;
using Meitou.Simulation.Carrying;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>
    /// Between ticks: the world's messages and stealth knock-outs. This player's (player 0's) go to the HUD and the log; while hosting, the others' go to
    /// their players (a message for everyone to every client too) as events. The fights hostility started and the population's news go to the log.
    /// </summary>
    void WorldNews()
    {
        var world = session.World;
        foreach (var message in world.TakeMessages())
        {
            if (message.For(0))
            {
                Console.WriteLine($"relations {message.Text}");
                player.Notice(message.Text);
            }
            if (netHost is { } host && message.Player != 0) host.Send(message.Player, new MessageNet(message.Text));
        }
        // Stealth knock-outs: the original's floating "*Thunk*" (a hit) or "Failed!" over the attacker, here as a notice to the attacker's owner.
        if (world.Systems.OfType<CarrySystem>().FirstOrDefault() is { Knockouts.Count: > 0 } carry)
            foreach (var k in carry.TakeKnockouts())
            {
                if (!world.Characters.IsAlive(k.Attacker) || world.Characters.Cold(k.Attacker.Slot) is not { IsPlayer: true } attacker) continue;
                int owner = world.OwnerOf(k.Attacker);
                if (owner == 0) player.Notice(KnockoutText(attacker.Name, k), !k.Success);
                else if (owner > 0) netHost?.Send(owner, new KnockoutNet(k));
            }
        if (population is not null)
            foreach (var line in population.TakeTownLog()) Console.WriteLine($"town      {line}");
        if (population is not null)
            foreach (var w in population.TakeWildLog())
                Console.WriteLine($"wild      tick {w.Tick}: {w.Squad} ({w.Members}) from the {(w.Kind == Meitou.Simulation.WildKind.Nest ? "nest" : "sector of")} {w.Place}");
        if (aggro is null || aggro.Log.Count == 0) return;
        var table = world.Characters;
        string Who(Meitou.Simulation.CharacterId id, int faction) =>
            $"{(table.IsAlive(id) ? table.Cold(id.Slot)!.Name : "?")} ({(popData is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Name : "no faction")})";
        foreach (var e in aggro.TakeLog())
            Console.WriteLine($"aggro     tick {e.Tick}: {Who(e.Attacker, e.AttackerFaction)} attacks {Who(e.Target, e.TargetFaction)} at {e.Distance:0}{(e.Joined > 0 ? $", {e.Joined} squad mate(s) join" : "")}");
    }

    /// <summary>The knock-out notice for <paramref name="name"/>'s attempt: <c>*Thunk*</c> or <c>Failed!</c> with the chance (the host's and a client's text).</summary>
    static string KnockoutText(string name, StealthKoEntry k) => $"{name}: {Loc.Tr(k.Success ? "*Thunk*" : "Failed!")} ({Math.Clamp(k.Chance, 0, 100):0}%)";
}
