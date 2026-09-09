using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ClrDebug;

// ---------------------------------------------------------------------------
// ICorDebug "STOP THE WORLD" COST PROXY  (Approach B pricing)
//
// Out-of-process managed debugger host. It:
//   1. Launches the Target under debugger control via dbgshim
//      (CreateProcessForLaunch + RegisterForRuntimeStartup), so we get a real
//      ICorDebug attached to a real CoreCLR.
//   2. On module load of Target.dll, resolves Program::Work and sets an
//      ICorDebugCode breakpoint at an IL offset INSIDE Work.
//   3. On each Breakpoint callback (which is dispatched only AFTER the runtime
//      has performed the cooperative stop of ALL managed threads), it:
//        t0 = now  (callback entry == process already stopped)
//        read one local from the active IL frame
//        t1 = now
//        Continue()
//        t2 = now (after Continue returns)
//      and records:
//        stop-cycle       = t2 - t0   (host-visible time the world is held)
//        local-read       = t1 - t0
//      The app-side sensor thread separately measures the freeze it actually
//      experienced (see Target/Program.cs).
//
// This is a PROXY: it prices ONE stop/read/continue cycle. It is not the
// full line-level feature.
// ---------------------------------------------------------------------------

class Host
{
    static readonly object s_gate = new object();
    static long s_hits;
    static long s_maxHits;
    static readonly System.Collections.Generic.List<double> s_stopUs = new();
    static readonly System.Collections.Generic.List<double> s_readUs = new();
    static readonly System.Collections.Generic.List<double> s_contUs = new();
    static readonly System.Collections.Generic.List<double> s_frameUs = new();
    static long s_badReads;
    static bool s_nonStop = (Environment.GetEnvironmentVariable("DI_NONSTOP") ?? "0") != "0";
    static long s_nonStopSet;
    static long s_nonStopFail;
    static long s_readSample = long.MinValue;
    static CorDebugProcess s_process;
    static ManualResetEventSlim s_done = new(false);
    static string s_targetModuleName = "Target";
    static int s_ilOffset;
    static bool s_bpSet;

    // dbgshim handle kept alive.
    static DbgShim s_dbgshim;
    static CorDebug s_cordbg;
    static IntPtr s_unregToken;
    static long s_events;
    static int s_pid;
    static object s_cbKeepAlive;
    static volatile bool s_exited;
    static long s_t0;
    static CorDebugFunctionBreakpoint s_bp;

    // Continue() for every non-breakpoint callback. ClrDebug never does this for us.
    static void SafeContinue(CorDebugController c, string what)
    {
        try { c.Continue(false); }
        catch (Exception ex) { if (Interlocked.Read(ref s_events) < 40) Log($"Continue({what}) EX: {ex.Message}"); }
    }

    static void Main(string[] args)
    {
        // args: <targetDll> <ilOffset> <maxHits> <readyFile> <dbgshimPath>
        string targetDll  = args[0];
        s_ilOffset        = int.Parse(args[1]);
        s_maxHits         = long.Parse(args[2]);
        string readyFile  = args[3];
        string dbgshimPath = args[4];

        Log($"host pid={Environment.ProcessId}");
        Log($"target={targetDll} ilOffset={s_ilOffset} maxHits={s_maxHits}");
        Log($"dbgshim={dbgshimPath}");
        Log($"Stopwatch.IsHighResolution={Stopwatch.IsHighResolution} freq={Stopwatch.Frequency}");

        IntPtr h = NativeLibrary.Load(dbgshimPath);
        s_dbgshim = new DbgShim(h);

        // Launch target suspended, under launch control, so we can register for
        // runtime startup before managed code runs.
        string dotnet = GetDotnetPath();
        string cmd = $"\"{dotnet}\" \"{targetDll}\" {Environment.GetEnvironmentVariable("DI_ITER") ?? "2000"} {Environment.GetEnvironmentVariable("DI_CALLGAP") ?? "5"}";
        Log($"launching: {cmd}");

        // Pass the ready-file env var to the child so the target waits for us.
        // CreateProcessForLaunch inherits the current environment when lpEnvironment==0.
        Environment.SetEnvironmentVariable("DI_HOST_READY_FILE", readyFile);

        var launch = s_dbgshim.CreateProcessForLaunch(cmd, bSuspendProcess: true, lpEnvironment: IntPtr.Zero, lpCurrentDirectory: null);
        int pid = launch.ProcessId;
        s_pid = pid;
        Log($"launched child pid={pid} (suspended)");

        var startupGate = new ManualResetEventSlim(false);
        HRESULT startupHr = HRESULT.E_FAIL;

        PSTARTUP_CALLBACK cb = (pCordb, param, hr) =>
        {
            try
            {
                startupHr = hr;
                if (hr == HRESULT.S_OK && pCordb != IntPtr.Zero)
                {
                    // ClrDebug 0.4.1 uses source-generated ComWrappers, NOT built-in
                    // COM marshalling (which is unsupported on Linux). Must use
                    // ClrDebug's own IUnknown->interface marshaller.
                    var raw = ClrDebug.Extensions.GetObjectForIUnknown<ICorDebug>(pCordb);
                    s_cordbg = new CorDebug(raw);
                    OnRuntimeStarted();
                }
                else
                {
                    Log($"startup callback hr={hr}");
                }
            }
            catch (Exception ex) { Log("startup callback EX: " + ex); }
            finally { startupGate.Set(); }
        };

        s_unregToken = s_dbgshim.RegisterForRuntimeStartup(pid, cb, IntPtr.Zero);
        Log("registered for runtime startup; resuming child");

        // Resume the suspended process so the CLR starts and fires the callback.
        s_dbgshim.ResumeProcess(launch.ResumeHandle);
        s_dbgshim.CloseResumeHandle(launch.ResumeHandle);

        if (!startupGate.Wait(TimeSpan.FromSeconds(60)))
        {
            Log("TIMEOUT waiting for runtime startup callback (60s). BLOCKED at attach.");
            Environment.Exit(20);
        }
        if (s_cordbg == null)
        {
            Log($"runtime startup failed hr={startupHr}. BLOCKED at CreateDebuggingInterface.");
            Environment.Exit(21);
        }

        // Wait for the run to finish (ExitProcess callback) or overall timeout.
        if (!s_done.Wait(TimeSpan.FromSeconds(180)))
            Log("overall timeout (180s); reporting what we have");

        Report();
    }

    static void OnRuntimeStarted()
    {
        Log("runtime started; wiring managed callback");
        var cb = new CorDebugManagedCallback();

        // IMPORTANT (verified by reflecting ClrDebug 0.4.1): CorDebugManagedCallback
        // .HandleEvent<T> only raises OnAnyEvent and then the typed handler; it
        // NEVER calls Continue and IGNORES e.Continue. Every ICorDebug callback
        // arrives with the process STOPPED, so the host must call Continue itself
        // for EVERY event or the target hangs forever.
        cb.OnCreateProcess += (s, e) =>
        {
            s_process = e.Process;
            Log("CreateProcess");
            SafeContinue(e.Controller, "CreateProcess");
        };
        cb.OnLoadModule += (s, e) =>
        {
            try { TryArmBreakpoint(e.Module); } catch (Exception ex) { Log("arm EX: " + ex); }
            SafeContinue(e.Controller, "LoadModule");
        };
        cb.OnBreakpoint += (s, e) => HandleBreakpoint(e);   // continues internally, timed
        cb.OnExitProcess += (s, e) =>
        {
            Log("ExitProcess");
            s_exited = true;
            s_done.Set();
        };
        cb.OnAnyEvent += (s, e) =>
        {
            // NOTE: OnAnyEvent is raised AFTER the typed handler in ClrDebug 0.4.1
            // (proven empirically: t0-from-here produced exactly the hit-to-hit
            // period, not the stop duration). So we do NOT time from here.
            if (e.Kind == CorDebugManagedCallbackKind.Breakpoint) return;

            long n = Interlocked.Increment(ref s_events);
            if (n <= 25) Log($"  evt#{n} {e.Kind}");
            // Continue for every event kind that has no dedicated handler above.
            switch (e.Kind)
            {
                case CorDebugManagedCallbackKind.CreateProcess:
                case CorDebugManagedCallbackKind.LoadModule:
                case CorDebugManagedCallbackKind.ExitProcess:
                    return;   // handled by its own handler
                default:
                    SafeContinue(e.Controller, e.Kind.ToString());
                    return;
            }
        };

        s_cordbg.Initialize();
        s_cordbg.SetManagedHandler(cb);
        s_cbKeepAlive = cb;
        Log("managed handler set");

        // Out-of-process attach pattern (dotnet/diagnostics): the ICorDebug handed
        // to the startup callback is NOT yet bound to the process. You must call
        // DebugActiveProcess to actually attach the debugger.
        try
        {
            s_process = s_cordbg.DebugActiveProcess(s_pid, win32Attach: false);
            Log($"DebugActiveProcess ok; process id={s_process.Id}");
        }
        catch (Exception ex)
        {
            Log("DebugActiveProcess FAILED (BLOCKED at attach): " + ex.Message);
            s_done.Set();
            return;
        }
        Log("attached; runtime will now dispatch events");
    }

    static void TryArmBreakpoint(CorDebugModule module)
    {
        if (s_bpSet) return;
        string name;
        try { name = module.Name; } catch { return; }
        string baseName = Path.GetFileNameWithoutExtension(name ?? "");
        if (!string.Equals(baseName, s_targetModuleName, StringComparison.OrdinalIgnoreCase))
            return;

        Log($"target module loaded: {name}");

        // Resolve Program type + Work method via metadata.
        var mdiObj = module.GetMetaDataInterface(typeof(IMetaDataImport).GUID);
        var import = new MetaDataImport((IMetaDataImport)mdiObj);

        mdTypeDef typeDef = import.FindTypeDefByName("Program", new mdToken(0));
        Log($"Program typeDef=0x{(int)typeDef:X}");

        mdMethodDef workToken;
        var hr = import.TryFindMethod(typeDef, "Work", IntPtr.Zero, 0, out workToken);
        if (hr != HRESULT.S_OK)
        {
            // Fallback: enumerate methods with name.
            IntPtr hEnum = IntPtr.Zero;
            var buf = new mdMethodDef[16];
            int c;
            import.TryEnumMethodsWithName(ref hEnum, typeDef, "Work", buf, out c);
            import.CloseEnum(hEnum);
            if (c > 0) workToken = buf[0]; else { Log("could not resolve Work method"); return; }
        }
        Log($"Work methodDef=0x{(int)workToken:X}");

        CorDebugFunction func = module.GetFunctionFromToken(workToken);
        CorDebugCode ilCode = func.ILCode;
        int ilSize = ilCode.Size;
        int off = s_ilOffset;
        if (off >= ilSize) { off = Math.Max(0, ilSize / 2); Log($"requested IL offset >= size {ilSize}; using {off}"); }

        // CONTROL MODE: attach the debugger exactly as normal but never arm the
        // breakpoint. Isolates "debugger merely attached" from "breakpoint hit".
        bool armed = (Environment.GetEnvironmentVariable("DI_ARM") ?? "1") != "0";
        CorDebugFunctionBreakpoint bp = null;
        if (armed)
        {
            bp = ilCode.CreateBreakpoint(off);
            bp.Activate(true);
        }
        else Log("CONTROL RUN: DI_ARM=0, breakpoint NOT armed (attached-only baseline)");
        s_bp = bp;
        s_ilOffset = off;
        s_bpSet = true;
        Log($"BREAKPOINT at Work IL offset {off} (ilSize={ilSize}); armed={armed}.");

        // Signal the target it may start its hot loop.
        string readyFile = Environment.GetEnvironmentVariable("DI_HOST_READY_FILE");
        if (!string.IsNullOrEmpty(readyFile))
        {
            File.WriteAllText(readyFile, "ready");
            Log("wrote host-ready file: " + readyFile);
        }
    }

    static void HandleBreakpoint(BreakpointCorDebugManagedCallbackEventArgs e)
    {
        // Entry to the typed breakpoint handler is the earliest managed instant at
        // which we can observe the already-stopped world.
        long t0 = Stopwatch.GetTimestamp();

        long readOk = -1;
        long tFrame = t0;
        try
        {
            CorDebugThread thread = e.Thread;
            CorDebugFrame frame = thread.ActiveFrame;
            tFrame = Stopwatch.GetTimestamp();
            // ClrDebug's CorDebugFrame.New() already returns the most-derived
            // wrapper (CorDebugILFrame for managed IL frames), so a plain cast
            // is the QueryInterface-equivalent here.
            var ilFrame = frame as CorDebugILFrame;
            if (ilFrame != null)
            {
                // local index 0 == `local` in Work (first declared local).
                CorDebugValue val = ilFrame.GetLocalVariable(0);
                // Extensions.As<T> is ClrDebug's ComWrappers-aware QI helper.
                var gv = ClrDebug.Extensions.As<CorDebugGenericValue>(val);
                if (gv != null)
                {
                    unsafe
                    {
                        int v;
                        gv.GetValue((IntPtr)(&v));
                        readOk = v;
                    }
                }
                else if (Interlocked.Read(ref s_hits) < 2) Log("As<CorDebugGenericValue> returned null; valType=" + val.GetType().Name);
            }
            else if (Interlocked.Read(ref s_hits) < 2) Log("ActiveFrame not an IL frame: " + frame?.GetType().Name);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref s_badReads);
            if (Interlocked.Read(ref s_hits) < 3) Log("read local EX: " + ex.Message);
        }

        long t1 = Stopwatch.GetTimestamp();

        // ---- NON-STOP EXPERIMENT (DI_NONSTOP=1) ----
        // GDB "non-stop mode" parity attempt. A theory raised in review: instead of freezing every
        // managed thread for the whole read, mark ONLY the hitting thread THREAD_SUSPEND and then
        // Continue the process, so the other threads run on. SetDebugState's own docs say the state
        // "represents the debug state if the process were to be continued", and states "last across
        // continues" -- which is exactly the primitive GDB non-stop needs.
        // If this works, the sensor thread (which never hits the breakpoint) should STOP freezing.
        if (s_nonStop)
        {
            try
            {
                // Everything except the hitting thread runs; the hitting thread stays parked.
                e.Controller.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, null);
                e.Thread.DebugState = CorDebugThreadState.THREAD_SUSPEND;
                Interlocked.Increment(ref s_nonStopSet);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref s_hits) < 3) Log("NONSTOP SetDebugState EX: " + ex.Message);
                Interlocked.Increment(ref s_nonStopFail);
            }
        }

        // Continue: resume all managed threads.
        try { e.Controller.Continue(false); }
        catch (Exception ex) { Log("Continue EX: " + ex.Message); }

        // Release the parked thread now that the process is running again, so the target can make
        // progress and we measure the freeze other threads felt, not a permanent deadlock.
        if (s_nonStop)
        {
            try { e.Thread.DebugState = CorDebugThreadState.THREAD_RUN; }
            catch (Exception ex) { if (Interlocked.Read(ref s_hits) < 3) Log("NONSTOP resume EX: " + ex.Message); }
        }

        long t2 = Stopwatch.GetTimestamp();

        double toUs = 1_000_000.0 / Stopwatch.Frequency;
        double stopUs = (t2 - t0) * toUs;
        double readUs = (t1 - t0) * toUs;
        double contUs = (t2 - t1) * toUs;
        double frameUs = (tFrame - t0) * toUs;

        long n = Interlocked.Increment(ref s_hits);
        lock (s_gate)
        {
            s_stopUs.Add(stopUs);
            s_readUs.Add(readUs);
            s_contUs.Add(contUs);
            s_frameUs.Add(frameUs);
            if (readOk != -1) s_readSample = readOk;
        }
        if (n <= 5 || n % 500 == 0)
            Log($"hit#{n} stop={stopUs:F1}us frame={frameUs:F1}us read={readUs:F1}us cont={contUs:F1}us local={readOk}");

        if (n == s_maxHits && s_maxHits > 0)
        {
            // Deactivate the breakpoint (cheap, we're already past Continue) so the
            // target can finish its loop at full speed and print ITS side of the
            // measurement, then report. We do NOT detach: detaching from inside a
            // callback thread is racy and we do not need it.
            try { s_bp?.Activate(false); Log("reached maxHits; breakpoint deactivated, letting target run out"); }
            catch (Exception ex) { Log("deactivate EX: " + ex.Message); }
        }
    }

    static void Report()
    {
        double[] stop, read, cont, frm;
        lock (s_gate) { stop = s_stopUs.ToArray(); read = s_readUs.ToArray(); cont = s_contUs.ToArray(); frm = s_frameUs.ToArray(); }
        Array.Sort(stop); Array.Sort(read); Array.Sort(cont); Array.Sort(frm);
        Log("==== HOST-SIDE STOP-THE-WORLD REPORT ====");
        Log($"hits={stop.Length} badReads={Interlocked.Read(ref s_badReads)} lastLocalRead={Interlocked.Read(ref s_readSample)}");
        if (stop.Length > 0)
        {
            Log($"STOP-CYCLE us  (callback-entry -> Continue returns): min={stop[0]:F1} p50={P(stop,50):F1} p90={P(stop,90):F1} p99={P(stop,99):F1} max={stop[^1]:F1} mean={Mean(stop):F1}");
            Log($"LOCAL-READ us  (frame + local read):                min={read[0]:F1} p50={P(read,50):F1} p90={P(read,90):F1} p99={P(read,99):F1} max={read[^1]:F1} mean={Mean(read):F1}");
            Log($"  GET-FRAME us (ActiveFrame only):                  min={frm[0]:F1} p50={P(frm,50):F1} p90={P(frm,90):F1} p99={P(frm,99):F1} max={frm[^1]:F1} mean={Mean(frm):F1}");
            Log($"CONTINUE us    (Continue() call only):              min={cont[0]:F1} p50={P(cont,50):F1} p90={P(cont,90):F1} p99={P(cont,99):F1} max={cont[^1]:F1} mean={Mean(cont):F1}");
        }
        Log("==== END HOST REPORT ====");
    }

    static double P(double[] s, double p)
    {
        if (s.Length == 0) return 0;
        int i = (int)Math.Ceiling(p / 100.0 * s.Length) - 1;
        if (i < 0) i = 0; if (i >= s.Length) i = s.Length - 1;
        return s[i];
    }
    static double Mean(double[] s) { double t = 0; foreach (var x in s) t += x; return s.Length == 0 ? 0 : t / s.Length; }

    static string GetDotnetPath()
    {
        string p = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "dotnet"))) return Path.Combine(p, "dotnet");
        foreach (var cand in new[] { "/usr/share/dotnet/dotnet", "/usr/bin/dotnet", "/root/.dotnet/dotnet" })
            if (File.Exists(cand)) return cand;
        return "dotnet";
    }

    static void Log(string m) => Console.WriteLine($"[host {DateTime.UtcNow:HH:mm:ss.fff}] {m}");
}
