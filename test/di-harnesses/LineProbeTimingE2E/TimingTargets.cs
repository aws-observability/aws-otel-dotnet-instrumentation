// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// GENERATED. Structurally identical to the proven Phase-2 SpikeTarget: no EH, no branches,
// sync, one local. In a Debug/no-optimize build the second statement boundary (`return y;`)
// is at IL offset 5 in EVERY one of these bodies, so the hardcoded offset works uniformly.
// ApplyTargetNN are used one-per-apply so we get N independent samples of the apply cost.
public static class TimingTargets
{
    public static int ApplyTarget0(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget1(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget2(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget3(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget4(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget5(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget6(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget7(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget8(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget9(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget10(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget11(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget12(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget13(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget14(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget15(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget16(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget17(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget18(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget19(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget20(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget21(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget22(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget23(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget24(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget25(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget26(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget27(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget28(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget29(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget30(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget31(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget32(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget33(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget34(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget35(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget36(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget37(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget38(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int ApplyTarget39(int x)
    {
        int y = x + 1;
        return y;
    }

    // ---- steady-state variants (probed once, then measured in tight loops) ----
    public static int SteadyBaseline(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int SteadyLegacy(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int SteadyGated(int x)
    {
        int y = x + 1;
        return y;
    }

    public static int SteadyUngated(int x)
    {
        int y = x + 1;
        return y;
    }

    // Hammered continuously by the Worker thread. A probe is applied to one of THESE while the worker
    // is mid-loop, so the worker's batch timings capture whatever an app thread feels at apply time.
    // There are several so the direct test can be repeated (one fresh, never-probed method per rep).
    public static int SteadyWorker0(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker1(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker2(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker3(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker4(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker5(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker6(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker7(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker8(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker9(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker10(int x)
    {
        int y = x + 1;
        return y;
    }
    public static int SteadyWorker11(int x)
    {
        int y = x + 1;
        return y;
    }
    public const int WorkerTargetCount = 12;

    public static int SteadyWorkerByIndex(int i, int x) => i switch
    {
        0 => SteadyWorker0(x),
        1 => SteadyWorker1(x),
        2 => SteadyWorker2(x),
        3 => SteadyWorker3(x),
        4 => SteadyWorker4(x),
        5 => SteadyWorker5(x),
        6 => SteadyWorker6(x),
        7 => SteadyWorker7(x),
        8 => SteadyWorker8(x),
        9 => SteadyWorker9(x),
        10 => SteadyWorker10(x),
        11 => SteadyWorker11(x),
        _ => -1,
    };

    public static int ApplyTargetByIndex(int i, int x) => i switch
    {
        0 => ApplyTarget0(x),
        1 => ApplyTarget1(x),
        2 => ApplyTarget2(x),
        3 => ApplyTarget3(x),
        4 => ApplyTarget4(x),
        5 => ApplyTarget5(x),
        6 => ApplyTarget6(x),
        7 => ApplyTarget7(x),
        8 => ApplyTarget8(x),
        9 => ApplyTarget9(x),
        10 => ApplyTarget10(x),
        11 => ApplyTarget11(x),
        12 => ApplyTarget12(x),
        13 => ApplyTarget13(x),
        14 => ApplyTarget14(x),
        15 => ApplyTarget15(x),
        16 => ApplyTarget16(x),
        17 => ApplyTarget17(x),
        18 => ApplyTarget18(x),
        19 => ApplyTarget19(x),
        20 => ApplyTarget20(x),
        21 => ApplyTarget21(x),
        22 => ApplyTarget22(x),
        23 => ApplyTarget23(x),
        24 => ApplyTarget24(x),
        25 => ApplyTarget25(x),
        26 => ApplyTarget26(x),
        27 => ApplyTarget27(x),
        28 => ApplyTarget28(x),
        29 => ApplyTarget29(x),
        30 => ApplyTarget30(x),
        31 => ApplyTarget31(x),
        32 => ApplyTarget32(x),
        33 => ApplyTarget33(x),
        34 => ApplyTarget34(x),
        35 => ApplyTarget35(x),
        36 => ApplyTarget36(x),
        37 => ApplyTarget37(x),
        38 => ApplyTarget38(x),
        39 => ApplyTarget39(x),
        _ => -1,
    };

    /// <summary>
    /// MULTI-PROBE TARGET: a deliberately long body with MANY interior statement boundaries, so
    /// several line probes can be placed at DIFFERENT offsets inside the SAME method. This is the
    /// "N probes, 1 method" scenario — the case where a single ReJIT should carry N edits.
    /// </summary>
    public static int MultiProbeTarget(int x)
    {
        int a = x + 1;
        int b = a * 2;
        int c = b - 3;
        int d = c + 4;
        int e = d * 5;
        int f = e - 6;
        int g = f + 7;
        int h = g * 8;
        return a ^ b ^ c ^ d ^ e ^ f ^ g ^ h;
    }
}
