// SPDX-License-Identifier: Apache-2.0
// Three sequential statements => three distinct interior statement boundaries, so we can request
// multiple line probes at DIFFERENT offsets in ONE method (the N2 question). No EH, no branches.
public static class WeaveTarget
{
    public static int Compute(int x)
    {
        int a = x + 1;   // boundary after stloc a
        int b = a + 10;  // boundary after stloc b
        int c = b + 100; // boundary after stloc c
        return c;
    }
}
