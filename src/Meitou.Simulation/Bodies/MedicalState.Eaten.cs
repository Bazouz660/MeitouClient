namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
    /// <summary>Passes of <see cref="BeingEaten"/> that found nothing left to eat (the original's medical <c>+0x15c</c>); past <see cref="EatenPassesToDeath"/> the character dies. Not saved.</summary>
    public int EatenPasses { get; private set; }

    /// <summary>The passes with nothing left to eat after which the character is eaten alive (<b>Observed</b>, <c>FUN_140650370</c>: more than 5).</summary>
    public const int EatenPassesToDeath = 5;

    /// <summary>
    /// The eating rate of a character being eaten, per second (<c>FUN_140650370</c>, <b>Observed</b>): the eater passes 1, times lerp(2, 0.4, toughness x 0.01) (the CharStats toughness slot
    /// <c>+0x90</c> times the stat scale <c>+0x34</c>), or times 5 instead when a medical flag (<c>+0x164</c>, meaning <b>Unknown</b>) is set, which Meitou never sets.
    /// </summary>
    public static float EatRate(float toughness, float statScale = 1) => 2f + (0.4f - 2f) * (toughness * statScale * 0.01f);

    /// <summary>
    /// One update of being eaten (<c>Task_EatPrisoner</c> calls the character's being-eaten virtual, <c>FUN_1404356f0</c> then <c>FUN_140650370</c>; docs/game/ai-tasks.md "Prisoner fate",
    /// <b>Observed</b>): nothing happens to a robot. The parts are walked from the last down: a limb that is a stump or a prosthetic is passed over (engine reading of the original's
    /// attached-limb test); the first part with h - 1.1 s above -M loses <paramref name="rate"/> x <paramref name="dt"/> from both its flesh and its treated pool, is checked as the
    /// medical tick checks a part (a vital part destroyed kills), and the update ends. A limb with nothing left is cut off on the way when <paramref name="limbLoss"/> (the original's global
    /// flag <c>DAT_142133504</c>, read as the dismemberment setting). When no part is left to eat the pass is counted; past <see cref="EatenPassesToDeath"/> the character dies
    /// (<see cref="DeathCause.Eaten"/>, the original's death cause "eaten"). True when it is eaten alive by this call.
    /// </summary>
    public bool BeingEaten(float rate, float dt, in MedicalContext ctx, bool limbLoss, List<MedicalEvent>? events = null)
    {
        if (Dead || ctx.Race.IsRobot) return false;
        float bite = rate * dt;
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var p = parts[i];
            bool limb = p.Template.LimbSlot is >= 0 and < 4;
            if (limb && p.Limb is LimbState.Stump or LimbState.Replaced) continue;
            if (p.Flesh - 1.1f * p.Stun > -p.MaxHp)
            {
                p.Flesh -= bite;
                p.Bandage -= bite;
                CheckPart(i, ctx, events, inHitPath: false, Standing);
                return Dead && Cause == DeathCause.Eaten;
            }
            if (limbLoss && limb && p.Limb is LimbState.Original or LimbState.Crushed && p.Template.Severance) Sever(i, ctx, Standing, events);
        }
        if (EatenPasses > EatenPassesToDeath)
        {
            Die(DeathCause.Eaten, events);
            return true;
        }
        EatenPasses++;
        return false;
    }
}
