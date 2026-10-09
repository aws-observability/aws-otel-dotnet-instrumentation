// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// ASYNC spike target (DECISION B core). The C# compiler rewrites this async method into a hidden
// state-machine struct `<Compute>d__N` with a `MoveNext()` method. The local `y` MUST survive the
// `await`, so the compiler HOISTS it from a stack slot to a FIELD on the state machine (a mangled
// name like `<y>5__1`). The user's source lines live in MoveNext, NOT in this method body.
//
// The spike proves the forked profiler can read that HOISTED FIELD off the state machine at an
// interior line in MoveNext (post-await) and pass its real value to a managed callback.
using System.Threading.Tasks;

public static class AsyncSpikeTarget
{
    public static async Task<int> Compute(int x)
    {
        int y = x + 1;          // L17: y assigned BEFORE the await -> hoisted to <y>5__1 to survive it
        await Task.Yield();     // L18: suspension point; MoveNext re-enters here as a continuation
        int z = y * 2;          // L19: reads hoisted y after resuming; POSITIVE inject site is here
        return z;               // L20
    }
}
