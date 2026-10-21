using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>
/// Which characters one client is sent, recomputed at every send (docs/net.md, "Interest, delta, replicator and mirror"):
/// <list type="bullet">
/// <item>always every player character (<see cref="CharacterSnapshot.IsPlayer"/>: co-op squad bars) and everything the client's UI names (selected and
/// inspected characters, the trade customer and trader, the conversation's ids, the dialogue request's ids, the viewer's squad members);</item>
/// <item>nearby characters: within <c>R = min(DrawDistance, 8000) + 300</c> of the eye on X/Z, kept until beyond <c>R + 300</c> (hysteresis);</item>
/// <item>one step of carry closure: a member's carrier and carried body.</item>
/// </list>
/// Each member gets a weight for the priority order: 1 for UI references, the viewer's own characters and anything closer than 1500; 0.5 closer than 3500;
/// 0.25 beyond. Members keep the snapshot's (slot) order.
/// </summary>
internal sealed class InterestSet
{
    public const float MaxRadius = 8000f, Margin = 300f, NearDistance = 1500f, MidDistance = 3500f;

    HashSet<CharacterId> members = [];
    HashSet<CharacterId> next = [];
    readonly HashSet<CharacterId> ui = [];
    readonly Dictionary<CharacterId, int> index = [];
    readonly List<bool> chosen = [];
    readonly List<int> extra = [];

    public IReadOnlySet<CharacterId> Members => members;

    public void Clear() => members.Clear();

    /// <summary>Fills <paramref name="into"/> with the members of <paramref name="view"/> for a client at <paramref name="camera"/>, in snapshot order, with their weights.</summary>
    public void Update(WorldSnapshot view, ViewState camera, int player, List<(CharacterSnapshot Character, float Weight)> into)
    {
        into.Clear();
        ui.Clear();
        if (view.Trade is { } trade) { ui.Add(trade.Customer); ui.Add(trade.Trader); }
        if (view.Conversation is { } talk) { ui.Add(talk.Npc); ui.Add(talk.Player); ui.Add(talk.Speaker); }
        if (view.DialogueRequest is { } request) { ui.Add(request.Player); ui.Add(request.Npc); }
        foreach (var squad in view.PlayerSquads)
            foreach (var id in squad.Members) ui.Add(id);

        float draw = float.IsFinite(camera.DrawDistance) ? Math.Clamp(camera.DrawDistance, 0f, MaxRadius) : 0f;
        float enter = draw + Margin, leave = enter + Margin;
        float enter2 = enter * enter, leave2 = leave * leave;

        var characters = view.Characters;
        index.Clear();
        chosen.Clear();
        for (int i = 0; i < characters.Count; i++)
        {
            var c = characters[i];
            index.TryAdd(c.Id, i);
            float d2 = Distance2(c, camera);
            bool referenced = c.Selected || c.Inspected || ui.Contains(c.Id);
            chosen.Add(c.IsPlayer || referenced || d2 <= enter2 || (members.Contains(c.Id) && d2 <= leave2));
        }
        // One step of closure over carrying, from the members chosen above (a body found this way does not bring its own links).
        extra.Clear();
        for (int i = 0; i < characters.Count; i++)
        {
            if (!chosen[i]) continue;
            var c = characters[i];
            if (c.CarriedBy != CharacterId.None && index.TryGetValue(c.CarriedBy, out int carrier)) extra.Add(carrier);
            if (c.Carrying != CharacterId.None && index.TryGetValue(c.Carrying, out int carried)) extra.Add(carried);
        }
        foreach (int i in extra) chosen[i] = true;

        next.Clear();
        for (int i = 0; i < characters.Count; i++)
        {
            if (!chosen[i]) continue;
            var c = characters[i];
            if (!next.Add(c.Id)) continue;
            float d2 = Distance2(c, camera);
            bool important = c.Selected || c.Inspected || ui.Contains(c.Id) || (player >= 0 && c.Owner == player) || d2 < NearDistance * NearDistance;
            into.Add((c, important ? 1f : d2 < MidDistance * MidDistance ? 0.5f : 0.25f));
        }
        (members, next) = (next, members);
    }

    static float Distance2(CharacterSnapshot c, ViewState camera)
    {
        float dx = c.Position.X - camera.Eye.X, dz = c.Position.Z - camera.Eye.Z;
        float d2 = dx * dx + dz * dz;
        return float.IsNaN(d2) ? float.PositiveInfinity : d2;
    }
}
