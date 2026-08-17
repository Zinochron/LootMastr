namespace LootMastr.Planning.Dps;

/// <summary>
/// The three level constants every term of the damage formula divides by.
///
/// <b>SUB and DIV are read from the game</b> — they are <c>ParamGrow.BaseSpeed</c> and
/// <c>ParamGrow.LevelModifier</c>, which is not what those columns are called but is exactly what
/// they hold: 380/1300 at level 80, 400/1900 at 90, 420/2780 at 100. Confirmed at all three.
///
/// <b>MAIN is not in that sheet</b> and is the one constant left here. It is not a guess either: a
/// level 100 paladin's untouched stats measured 421 dexterity, 441 mind and 265 intelligence
/// against job modifiers of 95, 100 and 60, which is <c>floor(440 × mod / 100)</c> plus a clan
/// bonus of one to three every time — three independent confirmations of 440.
///
/// A level with no MAIN gets no estimate rather than a borrowed one. Everything this plugin plans
/// for happens at the cap, so that costs nothing and saves rating somebody on numbers from the
/// wrong expansion.
/// </summary>
public readonly record struct LevelTable(int Level, int Main, int Sub, int Div)
{
    /// <summary>MAIN by level. Only the levels a savage tier is ever run at.</summary>
    public static int? MainFor(int level) => level switch
    {
        80 => 340,
        90 => 390,
        100 => 440,
        _ => null,
    };

    /// <summary>
    /// With SUB and DIV as the game reports them. This is the one to use — the numbers come from
    /// <c>ParamGrow</c> and cannot drift out of date with a patch.
    /// </summary>
    public static LevelTable? For(int level, int sub, int div)
    {
        if (MainFor(level) is not { } main || sub <= 0 || div <= 0)
            return null;

        return new LevelTable(level, main, sub, div);
    }

    /// <summary>
    /// Without the game, for the harness. The same numbers the sheet holds, written down once so a
    /// pure test does not need an excel reader.
    /// </summary>
    public static LevelTable? Known(int level) => level switch
    {
        80 => new LevelTable(80, 340, 380, 1300),
        90 => new LevelTable(90, 390, 400, 1900),
        100 => new LevelTable(100, 440, 420, 2780),
        _ => null,
    };
}
