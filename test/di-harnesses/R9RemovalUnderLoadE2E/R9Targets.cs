// SPDX-License-Identifier: Apache-2.0
// Targets for R9 (removal-under-load) and gap-2 (>2 probes, mixed emission modes).
//
// DISCIPLINE: every method here is a SEQUENCE OF SIMPLE STATEMENTS assigning to distinct locals, so
// the IL has one `stloc.N` per statement and the statement boundaries are the offsets right AFTER
// each stloc. Offsets are DISCOVERED by walking the real IL at runtime (see Program.cs) — never
// guessed. Prior spikes failed by guessing offsets that landed on operand bytes mid-instruction.

public static class R9Targets
{
    // 5 interior statement boundaries => room for >2 probes (gap 2) with the removal test needing
    // at least 3 co-located probes (remove the middle one, assert BOTH neighbours survive).
    // Slots: 0=a 1=b 2=c 3=d 4=e. Compute(1) => a=2 b=12 c=112 d=1112 e=11112, returns 11112.
    public static int Hot(int x)
    {
        int a = x + 1;
        int b = a + 10;
        int c = b + 100;
        int d = c + 1000;
        int e = d + 10000;
        return e;
    }

    // Separate target for the mixed-emission-mode test, so a failure there cannot be confused with
    // an R9 removal failure on Hot(). Same shape/slot layout.
    public static int Mixed(int x)
    {
        int a = x + 1;
        int b = a + 10;
        int c = b + 100;
        int d = c + 1000;
        return d;
    }

    // Third target: the survivor-integrity control. Never has a probe removed from it; if its probe
    // count or value ever changes while Hot() is being churned, the removal leaked across methods.
    public static int Untouched(int x)
    {
        int a = x + 1;
        int b = a + 10;
        return b;
    }
}
