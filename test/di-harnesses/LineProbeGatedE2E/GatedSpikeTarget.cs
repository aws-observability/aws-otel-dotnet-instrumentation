// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// Two structurally-identical interior-insertion targets for the BOX-GATE spike (DECISION A).
// Same shape as the proven Phase-2 SpikeTarget (no EH, no branches, sync), so the interior
// statement boundary is at the SAME IL offset (5) — `return y;`, empty stack. We use two separate
// methods so one process can weave the GATED sequence into one and the UNGATED (always-box)
// sequence into the other, and measure their per-call allocation side by side.
public static class GatedSpikeTarget
{
    // GATED probe target: fork weaves
    //   ldc.i4 probeId; call ShouldCapture; brfalse SKIP; ldc.i4 probeId; ldc.i4 <v>; box; call Capture; SKIP:
    public static int HotGated(int x)
    {
        int y = x + 1;   // statement boundary A
        return y;        // statement boundary B  <-- inject just before the ldloc/ret here (offset 5)
    }

    // UNGATED contrast target: fork weaves ldc.i4 probeId; ldc.i4 <v>; box; call Capture (no gate).
    public static int HotUngated(int x)
    {
        int y = x + 1;   // statement boundary A
        return y;        // statement boundary B  <-- offset 5
    }
}
