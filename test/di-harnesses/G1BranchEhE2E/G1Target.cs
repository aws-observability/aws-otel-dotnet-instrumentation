// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// G1 gate targets: REAL control flow that forces Export() to relocate branch targets and
// recompute EH clause spans when an interior insertion shifts byte offsets.
//   - Sum:     forward branches (if/else) + a backward loop branch. Injecting EARLY in the loop
//              body shifts the offsets of the branches that jump PAST the insertion, so Export()
//              must recompute BOTH forward branch deltas and the backward loop branch delta.
//   - Guarded: try/catch/finally. Injecting inside the try (and separately inside the catch)
//              shifts the byte spans of the EH clauses, so Export() must recompute TryOffset/
//              TryLength/HandlerOffset/HandlerLength from the relocated instruction pointers.
public static class G1Target
{
    public static int Sum(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            if (i % 2 == 0)
            {
                total += i;   // inject at a statement boundary in/near the loop body here
            }
            else
            {
                total -= i;
            }
        }

        return total;
    }

    public static int Guarded(int x)
    {
        int r = 0;
        try
        {
            r = 100 / x;      // inject inside the try here
        }
        catch (System.DivideByZeroException)
        {
            r = -1;           // inject inside the catch here
        }
        finally
        {
            r += 1;
        }

        return r;
    }
}
