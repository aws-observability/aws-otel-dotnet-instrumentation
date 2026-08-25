// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace DynamicInstrumentation.NetCore;

/// <summary>
/// The methods Dynamic Instrumentation probes are configured against.
/// </summary>
/// <remarks>
/// IN ITS OWN FILE ON PURPOSE, AND THE LINE NUMBERS ARE PART OF THE TEST CONTRACT. A line-level probe is
/// configured as file + line, so a probe target sharing a file with anything that changes independently
/// (route registrations, new endpoints) would have its line numbers shifted by unrelated edits — the probe
/// then resolves to a different statement or refuses, and the failure looks like a DI bug rather than an
/// edit. Keeping the targets here means only a deliberate change to this file can move them.
///
/// Statements carry `@probe:` markers; the contract tests resolve marker -> line number by reading this
/// file, so inserting code above a target is safe as long as its marker travels with the statement.
///
/// Instance-free and non-generic because DI resolves a target by type name plus method name and parameter
/// count, and refuses constructors, `ref`/`out`/`in` parameters, struct receivers, and more than nine
/// parameters. The type name the agent binds against is CodeUnit + "." + ClassName, i.e.
/// "DynamicInstrumentation.NetCore" + "." + "ProbeTargets".
/// </remarks>
public static class ProbeTargets
{
    /// <summary>Two arguments and a non-void return — the baseline function-level capture.</summary>
    /// <param name="orderId">An order identifier, captured as a string argument.</param>
    /// <param name="quantity">A quantity, captured as an int argument.</param>
    /// <returns>The computed total.</returns>
    public static int ComputeOrderTotal(string orderId, int quantity)
    {
        var unitCost = 7; // @probe:unitCostAssigned
        var total = unitCost * quantity; // @probe:totalAssigned
        return total; // @probe:orderReturn
    }

    /// <summary>Single string argument, string return.</summary>
    /// <param name="name">The name to greet.</param>
    /// <returns>The greeting.</returns>
    public static string GetGreeting(string name)
    {
        var greeting = $"Hello, {name}"; // @probe:greetingAssigned
        return greeting;
    }

    /// <summary>Async: DI captures at task completion, and the awaited result is the return value.</summary>
    /// <param name="seed">The seed value.</param>
    /// <returns>The doubled seed.</returns>
    public static async Task<int> ComputeAsync(int seed)
    {
        await Task.Delay(1);
        var doubled = seed * 2; // @probe:doubledAssigned
        return doubled;
    }

    /// <summary>Throws, so the snapshot carries a `throwable` rather than a return value.</summary>
    /// <param name="reason">Text folded into the exception message.</param>
    public static void FailingOperation(string reason)
    {
        throw new InvalidOperationException($"probe target failed: {reason}");
    }

    // TARGETS BELOW ARE APPENDED, AND MUST STAY APPENDED. The `@probe:` markers above resolve to line
    // numbers that are part of the line-level test contract, so a new target inserted between existing ones
    // would silently move a probe onto a different statement. Add to the end.

    /// <summary>A string longer than the enforced capture maximum, so truncation is observable.</summary>
    /// <param name="text">Text whose captured form must be clamped to MaxStringLength.</param>
    /// <returns>The untruncated length, so the app's own view differs from the captured one.</returns>
    public static int ProcessLongString(string text)
    {
        return text.Length;
    }

    /// <summary>A collection wider than the enforced capture maximum, so element capping is observable.</summary>
    /// <param name="items">Items whose captured form must be capped at MaxCollectionWidth.</param>
    /// <returns>The untruncated count.</returns>
    public static int ProcessLargeCollection(List<int> items)
    {
        return items.Count;
    }

    /// <summary>Target for a BREAKPOINT with MaxHits: called more often than the limit allows.</summary>
    /// <param name="callNumber">Which call this is, so snapshots can be told apart.</param>
    /// <returns>The call number, echoed.</returns>
    public static int LimitedFunction(int callNumber)
    {
        return callNumber;
    }
}
