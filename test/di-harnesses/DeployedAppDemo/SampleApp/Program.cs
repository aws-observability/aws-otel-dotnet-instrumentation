// An ordinary .NET console app. It has NO knowledge of Dynamic Instrumentation — no DI
// package reference, no capture code. It just runs business logic in a loop. Dynamic
// Instrumentation is enabled entirely from the OUTSIDE (ADOT profiler + plugin + env vars),
// exactly as it would be for a real customer application.

using MyCompany.Orders;

Console.WriteLine($"[app] SampleApp starting (pid {Environment.ProcessId}). No DI code here.");
Console.WriteLine("[app] running business loop; DI (if enabled) is applied by the ADOT profiler.");

var orders = new OrderService();
var checkout = new CheckoutService();
var bulk = new BulkService();
var inventory = new InventoryService();
var showcase = new ShowcaseService();

// Arguments sized to cross each capture limit so the snapshot shows the truncation signals, not just values:
// 500 chars > MaxStringLength 255, 50 items > MaxCollectionWidth 20, a 5-deep chain > MaxObjectDepth 3.
var showcaseText = new string('x', 500);
var showcaseItems = Enumerable.Range(0, 50).ToList();
var showcaseTags = new Dictionary<string, int> { ["alpha"] = 1, ["beta"] = 2, ["gamma"] = 3 };
var showcaseAddress = new Address();
var showcaseChain = new Link { Depth = 0, Next = new Link { Depth = 1, Next = new Link { Depth = 2, Next = new Link { Depth = 3, Next = new Link { Depth = 4 } } } } };
var showcaseSelfRef = new Link { Depth = 99 };
showcaseSelfRef.Next = showcaseSelfRef;

string CallShowcase(int i)
{
    var d = showcase.Describe(showcaseText, showcaseItems, showcaseTags, showcaseAddress, showcaseChain, showcaseSelfRef, null);

    // Caught here so the throwable probe has a live target without taking the app down.
    try
    {
        return d + showcase.Fail($"tick-{i}");
    }
    catch (InvalidOperationException)
    {
        return d;
    }
}

// BULK MODE: call every BulkService method continuously BEFORE the probes are applied, so each one
// is genuinely JIT-compiled. RequestReJIT only recompiles methods that ALREADY have JIT'd code — an
// uncalled method is silently skipped ("Request ReJIT done for 1 methods" on an N-probe batch), which
// is exactly what made the earlier microbenchmark attempt at bulk apply meaningless.
// WARM THE TARGET METHODS BEFORE ANY PROBE CAN APPLY, then announce it.
//
// RequestReJIT only recompiles a method that ALREADY has JIT-compiled code; an uncalled method is
// silently skipped — the profiler still logs "Request ReJIT done for 1 methods" and no rewrite happens.
// DI's first configuration poll is kicked off from the startup hook, i.e. BEFORE this Main body runs, so
// without this warm-up the apply races the first invocation and usually loses. The harness waits for
// TARGETS-WARM before creating any configuration, which makes the ordering deterministic instead of a
// coin flip — and also matches the real operator story: the app is already serving when a probe is added.
for (int warm = 0; warm < 50; warm++)
{
    Sink += orders.Process($"warm-{warm}").Length;
    Sink += checkout.Complete($"warm-{warm}").Length;
    Sink += inventory.Reserve(warm).Length;
    Sink += inventory.DescribeNote(warm).Length;
    Sink += inventory.DescribeStamp(warm).Length;
    Sink += inventory.DescribeRatio(warm).Length;
    Sink += inventory.DescribeAll(warm).Length;
    Sink += inventory.ReserveAsync(warm).Result.Length;
    Sink += inventory.DescribeAsync(warm).Result.Length;
    Sink += CallShowcase(warm).Length;
}

Console.WriteLine("[app] TARGETS-WARM");
Console.Out.Flush();

bool bulkMode = Environment.GetEnvironmentVariable("DI_BULK_MODE") == "1";
int bulkMethods = int.TryParse(Environment.GetEnvironmentVariable("DI_BULK_METHODS"), out var bm) ? bm : 20;

if (bulkMode)
{
    Console.WriteLine($"[app] BULK MODE: pre-warming {bulkMethods} BulkService methods so all are JIT-compiled");
    for (int warm = 0; warm < 500; warm++)
    {
        for (int m = 0; m < bulkMethods; m++) { Sink += bulk.Invoke(m, warm).Length; }
    }

    Console.WriteLine($"[app] pre-warm done ({500 * bulkMethods:N0} calls); all {bulkMethods} methods are JIT'd");
    Console.WriteLine("[app] BULK-WARM-COMPLETE");
    Console.Out.Flush();
}

// Give the DI poller time to fetch configs + the profiler time to ReJIT, then keep calling
// the target methods so any applied probe/breakpoint has live invocations to capture.
// TICKS IS CONFIGURABLE because the operator-driven segment needs the app to still be serving traffic while
// a human reads a request, decides, and presses Enter. The automated run keeps the original 60 (~60s, which
// is all the assertions need); the interactive demo raises it so the presenter is never racing the app's exit.
int ticks = int.TryParse(Environment.GetEnvironmentVariable("DI_TICKS"), out var t) && t > 0 ? t : 60;
for (int i = 0; i < ticks; i++)
{
    var o = orders.Process($"order-{i}");
    var c = checkout.Complete($"cart-{i}");

    // Uninstrumented at boot; a probe gets attached to this by hand during STEP 5.
    var live = inventory.LiveDemo(i);

    // Line-probe target. Called on every tick so an applied probe has live invocations, and called with a
    // CHANGING quantity so the captured `total` proves the probe read the real local rather than a constant:
    // total == i * 7, so tick 3 must capture 21. A fixed argument would make a hardcoded value
    // indistinguishable from a genuine read.
    var r = inventory.Reserve(i);

    // Non-int local capture target. `id` varies per tick so each captured local carries a value derived
    // from it — a constant would be indistinguishable from a misread slot.
    var dn = inventory.DescribeNote(i);
    var ds = inventory.DescribeStamp(i);
    var dr = inventory.DescribeRatio(i);

    // Multi-local target: ONE config captures count/label/weight at a single line.
    var da = inventory.DescribeAll(i);

    // Async target: `total` is hoisted onto the state machine because it crosses the await, so the
    // probe must read a FIELD on the resumed continuation rather than a local slot.
    var ar = inventory.ReserveAsync(i).Result;

    // Async NON-INT hoisted locals: proves each hoisted field is boxed against its OWN type (or not at all).
    var dasync = inventory.DescribeAsync(i).Result;

    // Every captured-value shape in one call, plus a throwing target for the throwable branch.
    var sc = CallShowcase(i);

    // Keep every bulk method hot so an applied probe has live invocations to capture, and so the
    // rewritten body is entered promptly after the weave lands.
    if (bulkMode)
    {
        for (int rep = 0; rep < 50; rep++)
        {
            for (int m = 0; m < bulkMethods; m++) { Sink += bulk.Invoke(m, i).Length; }
        }
    }

    if (i % 10 == 0) { Console.WriteLine($"[app] tick {i}: {o} / {c} / {r}"); }
    Thread.Sleep(1000);
}

Console.WriteLine($"[app] done. (sink={Sink})");

// Keeps the pre-warm/hot-loop calls observably live so the JIT cannot elide them.
public partial class Program
{
    public static long Sink;
}

namespace MyCompany.Orders
{
    public class OrderService
    {
        public string Process(string orderId) => $"processed:{orderId}";
    }

    public class CheckoutService
    {
        public string Complete(string cartId) => $"completed:{cartId}";
    }

    /// <summary>
    /// Target for LINE-LEVEL probes. Unlike the expression-bodied methods above, this has a real
    /// multi-statement body so a probe can be placed at an interior source line.
    /// </summary>
    // WHY THIS SHAPE, deliberately:
    //  - `total` is a plain `int` LOCAL in a SYNCHRONOUS method — the simplest possible case, kept as the
    //    baseline. (The one-local, int-only limits it was originally written for are gone: DescribeAll now
    //    captures three locals at one line, DescribeNote/Stamp/Ratio cover other type families, and
    //    ReserveAsync/DescribeAsync cover locals hoisted onto an async state machine.)
    //  - The local is ASSIGNED on a line strictly BEFORE the probed line. A probe placed at or above the
    //    assignment reads the slot's default (0), which the async spike documented as the read-timing hazard.
    //  - The body is straight-line: no try/catch, no loop around the probed line. Interior insertion into
    //    real EH regions and branches is the highest remaining unproven risk, so it is deliberately NOT
    //    mixed into the first end-to-end proof — one variable at a time.
    //  - Not expression-bodied: an expression body compiles to a single sequence point, leaving no interior
    //    line to inject at.
    public class InventoryService
    {
        /// <summary>
        /// RESERVED FOR THE LIVE, OPERATOR-DRIVEN SEGMENT (STEP 5). Deliberately has NO configuration at
        /// startup, so when the presenter creates one by hand the audience sees a probe being attached to a
        /// method that was running uninstrumented seconds earlier — which is the whole claim of the feature.
        /// A target that already carried a config would have been woven during boot and would prove nothing.
        /// </summary>
        /// <param name="id">Tick number, so the captured values change every call.</param>
        /// <returns>A string derived from both locals.</returns>
        public string LiveDemo(int id)
        {
            var amount = id * 11;
            var label = $"live:{amount}"; // @live-probe-target: amount
            return label;
        }

        public string Reserve(int quantity)
        {
            var unitCost = 7;
            var total = quantity * unitCost;
            var label = $"reserved:{total}"; // @line-probe-target: total
            return label;
        }

        /// <summary>
        /// Target for NON-INT local capture. Each probed local is a different type family, because the
        /// int-only limitation was in the native box token and each family exercises a different branch of
        /// the fix.
        /// </summary>
        // WHY THESE THREE TYPES:
        //  - `note` is a STRING (reference type): must be passed with NO box at all. `box` on an object
        //    reference is invalid IL and would make the verifier reject the whole rewritten method, so this
        //    is the case that proves the no-box branch rather than merely a different token.
        //  - `stamp` is a DateTime (value type, NOT Int32): proves the box token is resolved from the
        //    local's own type. Under the old hardcoded System.Int32 token this would box a DateTime as an
        //    int — undefined behavior, not a clean failure.
        //  - `ratio` is a double: a second, differently-sized value type, so a pass cannot be an accident
        //    of DateTime happening to be pointer-sized.
        // One probed local per METHOD, not per line: InstrumentationKey is "{Type}.{Method}:{Line}" and does
        // NOT include the local name, so two configs capturing different locals at the SAME line collapse to
        // one registry entry. Separate methods keep the three cases independent and observable.
        public string DescribeNote(int id)
        {
            var note = $"item-{id}";
            var summary = $"note={note}"; // @line-probe-target: note
            return summary;
        }

        public string DescribeStamp(int id)
        {
            var stamp = new DateTime(2026, 1, 1).AddDays(id);
            var summary = $"stamp={stamp:yyyy-MM-dd}"; // @line-probe-target: stamp
            return summary;
        }

        public string DescribeRatio(int id)
        {
            var ratio = id * 1.5;
            var summary = $"ratio={ratio}"; // @line-probe-target: ratio
            return summary;
        }

        /// <summary>
        /// Target for MULTI-LOCAL capture: three locals of three different type families, all captured at
        /// ONE line by a SINGLE configuration.
        /// </summary>
        // This is the case the earlier separate-method workaround existed to avoid. It matters because it is
        // what an operator actually does — "show me the state at this line" — rather than creating one
        // config per variable. Mixed types on one line also prove the N-probes design carries each local's
        // own box token, which a single object[] of pre-boxed values would not distinguish.
        public string DescribeAll(int id)
        {
            var count = id * 3;
            var label = $"all-{id}";
            var weight = id * 0.25;
            var summary = $"{count}/{label}/{weight}"; // @line-probe-target-multi: count,label,weight
            return summary;
        }

        /// <summary>
        /// Target for ASYNC line-level capture: the probed local's lifetime crosses an <c>await</c>, so the
        /// compiler moves it out of the method entirely.
        /// </summary>
        // WHY THIS SHAPE, deliberately:
        //  - `total` is assigned BEFORE the await and read AFTER it, so its lifetime spans the suspension
        //    point. That is what forces Roslyn to rewrite it from a local into a FIELD `<total>5__N` on the
        //    generated state machine — `ldloc` cannot reach it, which is the entire difference from the sync
        //    case above and the reason async needed its own resolution path.
        //  - The probed line is AFTER the await, so the capture happens on the RESUMED execution — a
        //    different call stack and (usually) a different thread from the one that entered the method.
        //    Probing before the await would pass without ever exercising resumption.
        //  - `total` varies with the argument (i * 9), so a captured constant is distinguishable from a real
        //    read of the hoisted field. A fixed value would make a misread field look correct.
        //  - The app builds Release, where the state machine is a STRUCT and `ldarg.0` is a managed pointer
        //    rather than an object reference. That is the shape most likely to produce invalid IL if the
        //    emission were wrong, so it is the one worth proving end to end.
        public async Task<string> ReserveAsync(int quantity)
        {
            var unitCost = 9;
            var total = quantity * unitCost;
            await Task.Yield();
            var label = $"async-reserved:{total}"; // @line-probe-target-async: total
            return label;
        }

        /// <summary>
        /// Target for ASYNC capture of NON-INT hoisted locals — three type families, one config, one line.
        /// </summary>
        // WHY THIS EXISTS SEPARATELY FROM ReserveAsync: that method captures an `int`, and the native async
        // path used to box EVERY hoisted field against a hardcoded System.Int32 token. Boxing an Int32 as
        // System.Int32 is accidentally correct, so an int-only async test passes whether or not the box token
        // is actually resolved from the field's own type — it cannot tell the fixed bug from the bug.
        //
        // These three can:
        //   note  = System.String  -> reference type; a `box` here is invalid IL and the verifier would reject
        //                             the whole rewritten MoveNext, taking the method down.
        //   ratio = System.Double  -> value type that is NOT Int32; under the old hardcoded token this boxed a
        //                             double as an int, which is undefined behavior rather than a clean error.
        //   count = System.Int32   -> the control: proves the others' failure is about the token, not the path.
        // All three cross the await, so all three are hoisted fields rather than slots.
        public async Task<string> DescribeAsync(int id)
        {
            var note = $"async-item-{id}";
            var ratio = id * 2.5;
            var count = id * 11;
            await Task.Yield();
            var summary = $"{note}/{ratio}/{count}"; // @line-probe-target-async-multi: note,ratio,count
            return summary;
        }
    }

    /// <summary>
    /// 20 distinct, individually-probeable methods for the bulk-apply test. Each has a real body so
    /// the JIT cannot elide it; Invoke dispatches by index so the app can keep all of them hot
    /// without needing 20 separate call sites.
    /// </summary>
    public class BulkService
    {
        public string Handler0(int x) => $"h0:{(x * 3) + 1}";

        public string Handler1(int x) => $"h1:{(x * 5) + 2}";

        public string Handler2(int x) => $"h2:{(x * 7) + 3}";

        public string Handler3(int x) => $"h3:{(x * 11) + 4}";

        public string Handler4(int x) => $"h4:{(x * 13) + 5}";

        public string Handler5(int x) => $"h5:{(x * 17) + 6}";

        public string Handler6(int x) => $"h6:{(x * 19) + 7}";

        public string Handler7(int x) => $"h7:{(x * 23) + 8}";

        public string Handler8(int x) => $"h8:{(x * 29) + 9}";

        public string Handler9(int x) => $"h9:{(x * 31) + 10}";

        public string Handler10(int x) => $"h10:{(x * 37) + 11}";

        public string Handler11(int x) => $"h11:{(x * 41) + 12}";

        public string Handler12(int x) => $"h12:{(x * 43) + 13}";

        public string Handler13(int x) => $"h13:{(x * 47) + 14}";

        public string Handler14(int x) => $"h14:{(x * 53) + 15}";

        public string Handler15(int x) => $"h15:{(x * 59) + 16}";

        public string Handler16(int x) => $"h16:{(x * 61) + 17}";

        public string Handler17(int x) => $"h17:{(x * 67) + 18}";

        public string Handler18(int x) => $"h18:{(x * 71) + 19}";

        public string Handler19(int x) => $"h19:{(x * 73) + 20}";

        public string Invoke(int m, int x) => m switch
        {
            0 => this.Handler0(x),
            1 => this.Handler1(x),
            2 => this.Handler2(x),
            3 => this.Handler3(x),
            4 => this.Handler4(x),
            5 => this.Handler5(x),
            6 => this.Handler6(x),
            7 => this.Handler7(x),
            8 => this.Handler8(x),
            9 => this.Handler9(x),
            10 => this.Handler10(x),
            11 => this.Handler11(x),
            12 => this.Handler12(x),
            13 => this.Handler13(x),
            14 => this.Handler14(x),
            15 => this.Handler15(x),
            16 => this.Handler16(x),
            17 => this.Handler17(x),
            18 => this.Handler18(x),
            19 => this.Handler19(x),
            _ => this.Handler0(x),
        };
    }

    /// <summary>A plain object, so a captured value shows the `fields` shape.</summary>
    public class Address
    {
        public string City { get; set; } = "Seattle";

        public int Zip { get; set; } = 98109;
    }

    /// <summary>A chain deeper than MaxObjectDepth, so the tail reports not_captured_reason=DEPTH.</summary>
    public class Link
    {
        public int Depth { get; set; }

        public Link? Next { get; set; }
    }

    /// <summary>
    /// One method whose arguments cover EVERY shape a captured value can take, so a single probe shows the
    /// whole snapshot-body schema rather than one branch of it.
    /// </summary>
    /// <remarks>Arity 7, distinct from Fail's 1: co-located targets are told apart by parameter count.</remarks>
    public class ShowcaseService
    {
        public string Describe(string longText, List<int> manyItems, Dictionary<string, int> tags, Address address, Link chain, Link selfRef, string? missing)
        {
            var summary = $"{longText.Length}/{manyItems.Count}/{tags.Count}/{address.City}"; // @showcase-line: summary
            return summary;
        }

        /// <summary>Throws, so the snapshot carries captures.return.throwable instead of a return value.</summary>
        public string Fail(string reason)
        {
            throw new InvalidOperationException($"showcase failure: {reason}");
        }
    }
}
