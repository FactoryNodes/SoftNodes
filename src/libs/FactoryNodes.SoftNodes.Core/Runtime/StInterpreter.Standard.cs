namespace FactoryNodes.SoftNodes.Runtime;

/// <summary>Los bloques de función estándar que admite la librería maestra: TON, TOF, R_TRIG, F_TRIG y RS (más TP y SR).</summary>
public sealed partial class StInterpreter
{
    private static IEnumerable<(string Name, StType Type)> StandardBlockVars(string block)
    {
        var bool_ = StType.Bool;
        return block.ToUpperInvariant() switch
        {
            "TON" or "TOF" or "TP" => [("IN", bool_), ("PT", StType.Time), ("Q", bool_), ("ET", StType.Time)],
            "R_TRIG" or "F_TRIG" => [("CLK", bool_), ("Q", bool_)],
            "RS" => [("SET", bool_), ("RESET1", bool_), ("Q1", bool_)],
            "SR" => [("SET1", bool_), ("RESET", bool_), ("Q1", bool_)],
            _ => throw new InvalidOperationException($"Bloque estándar «{block}» desconocido.")
        };
    }

    private static bool Flag(FbInstance i, string name) => (bool)i.Vars[name].Value;

    private static long Number(FbInstance i, string name) => (long)i.Vars[name].Value;

    private static bool Memory(FbInstance i, string key) => i.State.TryGetValue(key, out var v) && (bool)v;

    private static long Mark(FbInstance i, string key) => i.State.TryGetValue(key, out var v) ? (long)v : 0;

    /// <summary>Una llamada a un bloque estándar: lee sus entradas, calcula sus salidas con el reloj del intérprete.</summary>
    private void StepStandard(FbInstance i, Frame frame)
    {
        switch (i.Type.Name)
        {
            case "TON":
            {
                var @in = Flag(i, "IN");
                var pt = Number(i, "PT");
                if (!@in)
                {
                    i.State["running"] = false;
                    i.Vars["ET"].Value = 0L;
                    i.Vars["Q"].Value = false;
                    break;
                }

                if (!Memory(i, "running"))
                {
                    i.State["running"] = true;
                    i.State["start"] = NowMs;
                }

                var elapsed = Math.Min(NowMs - Mark(i, "start"), pt);
                i.Vars["ET"].Value = elapsed;
                i.Vars["Q"].Value = elapsed >= pt;
                break;
            }

            case "TOF":
            {
                var @in = Flag(i, "IN");
                var pt = Number(i, "PT");
                if (@in)
                {
                    i.State["running"] = false;
                    i.Vars["ET"].Value = 0L;
                    i.Vars["Q"].Value = true;
                }
                else
                {
                    if (Memory(i, "prevIn"))
                    {
                        i.State["running"] = true;
                        i.State["start"] = NowMs;
                    }

                    if (Memory(i, "running"))
                    {
                        var elapsed = Math.Min(NowMs - Mark(i, "start"), pt);
                        i.Vars["ET"].Value = elapsed;
                        i.Vars["Q"].Value = elapsed < pt;
                        if (elapsed >= pt) i.State["running"] = false;
                    }
                    else
                    {
                        i.Vars["Q"].Value = false;
                    }
                }

                i.State["prevIn"] = @in;
                break;
            }

            case "TP":
            {
                var @in = Flag(i, "IN");
                var pt = Number(i, "PT");
                if (@in && !Memory(i, "prevIn") && !Memory(i, "running"))
                {
                    i.State["running"] = true;
                    i.State["start"] = NowMs;
                }

                if (Memory(i, "running"))
                {
                    var elapsed = Math.Min(NowMs - Mark(i, "start"), pt);
                    i.Vars["ET"].Value = elapsed;
                    i.Vars["Q"].Value = elapsed < pt;
                    if (elapsed >= pt && !@in) { i.State["running"] = false; i.Vars["ET"].Value = 0L; }
                }
                else
                {
                    i.Vars["Q"].Value = false;
                    i.Vars["ET"].Value = 0L;
                }

                i.State["prevIn"] = @in;
                break;
            }

            case "R_TRIG":
            {
                var clk = Flag(i, "CLK");
                i.Vars["Q"].Value = clk && !Memory(i, "m");
                i.State["m"] = clk;
                break;
            }

            case "F_TRIG":
            {
                var clk = Flag(i, "CLK");
                i.Vars["Q"].Value = !clk && !Memory(i, "m");
                i.State["m"] = clk;
                break;
            }

            case "RS":   // el RESET manda
                i.Vars["Q1"].Value = !Flag(i, "RESET1") && (Flag(i, "SET") || Flag(i, "Q1"));
                break;

            case "SR":   // el SET manda
                i.Vars["Q1"].Value = Flag(i, "SET1") || (!Flag(i, "RESET") && Flag(i, "Q1"));
                break;

            default:
                throw new InvalidOperationException($"Bloque estándar «{i.Type.Name}» sin implementar.");
        }
    }
}
