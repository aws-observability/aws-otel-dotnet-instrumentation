// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// The smallest possible interior-insertion target: no EH, no branches, sync (per DECISION-3).
// This holds Q1's residual risk (EH splitting / branch relocation) constant so the PoC tests
// ONLY interior insertion + runtime fire.
public static class SpikeTarget
{
    public static int Compute(int x)
    {
        int y = x + 1;   // statement boundary A
        return y;        // statement boundary B  <-- inject just before the ldloc/ret here
    }
}
