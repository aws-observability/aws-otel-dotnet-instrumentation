# .NET Framework manual reproductions — v4 migration

Two **separate** manual tests. Both use this one project (`NetFxV3Repro.csproj`), which
multi-targets `net462;net472` against **AWS SDK v3**. `dotnet build -c Release` produces:
- `bin\Release\net472\NetFxV3Repro.exe`  → used by Test 1
- `bin\Release\net462\NetFxV3Repro.exe`  → used by Test 2

The app makes a real S3 `GetBucketLocation` call in a loop and self-classifies the outcome:
it prints the **loaded `AWSSDK.Core` version** (v3 vs v4) and labels binding failures
(`MissingMethod`/`TypeLoad`/`FileLoad`) as `[APP BREAK]` vs. treating credential/network/
service errors as NOT-a-break. Read stdout + the SUMMARY line to classify.

Why AWS SDK stays **v3** in both tests: v4 requires net472+, so a net462 build can only use
v3. Keeping v3 across both keeps the app buildable on both TFMs and isolates the variable each
test is actually about.

---

## Test 1 — SDK version mismatch (net472): break vs. silent telemetry loss?
Question: a customer app on **AWS SDK v3** runs under our **v4** ADOT instrumentation — does
the app crash, or keep running with only AWS SDK telemetry dropped?
(We already confirmed .NET Core = silent loss. This tests whether netfx/GAC changes that.)

**Environment:** Windows with **.NET Framework 4.7.2+** installed. Windows Server 2019/2022
(ship 4.8) are fine. Needs .NET SDK to build, PowerShell (Admin) for the GAC install.

1. **Baseline — latest RELEASED ADOT (v3-based) + net472 app:** confirm instrumentation works.
   ```powershell
   .\bin\Release\net472\NetFxV3Repro.exe          # sanity: prints "v3 bound", app runs
   # Install the LAST RELEASED ADOT (download its psm1 + zip from GitHub Releases):
   Import-Module .\AWS.Otel.DotNet.Auto.psm1
   Install-OpenTelemetryCore
   Register-OpenTelemetryForCurrentSession -OTelServiceName "test1-baseline"
   $env:OTEL_TRACES_EXPORTER="console"
   .\bin\Release\net472\NetFxV3Repro.exe
   ```
   **Expect:** app runs; an `S3`/`GetBucketLocation` span appears in console output. → baseline good.

2. **Swap to OUR local v4 build + same net472 app:** the actual test.
   ```powershell
   Uninstall-OpenTelemetryCore                    # clear baseline from GAC first
   Import-Module .\AWS.Otel.DotNet.Auto.psm1      # our branch's module
   Install-OpenTelemetryCore -LocalPath "C:\path\to\aws-distro-...-windows.zip"   # our build.sh output
   Register-OpenTelemetryForCurrentSession -OTelServiceName "test1-v4"
   $env:OTEL_TRACES_EXPORTER="console"
   # Enable Fusion binding log first (see "Fusion log" section) to see which AWSSDK.Core wins.
   .\bin\Release\net472\NetFxV3Repro.exe
   ```
   **Classify:**
   - SUMMARY `0 failures` + app printed `v3 bound` (or `v4 BOUND`) and no `[APP BREAK]` →
     **SILENT LOSS** (check the S3 span is absent → v3 app + v4 instr = graceful on netfx).
   - Any `[APP BREAK] MissingMethod/TypeLoad/FileLoad` → **APP BREAK** (GAC v4 bound over the
     app's v3 and faulted the customer's own S3 call). This is the outcome we must gate against.

3. *(optional)* Upgrade the app to AWS SDK v4 (edit csproj to `AWSSDK.* 4.x`, rebuild net472),
   re-run under our v4 build → telemetry should return (confirms v4+v4 happy path on netfx).

---

## Test 2 — the 4.6.2 support drop (net462): does a 4.6.2 app break?
Question: our distro is now **net472-only**. Does a customer app **targeting net462** still
load/run under it, or does the missing 4.6.2 support break the app? (AWS SDK stays v3.)

**⚠️ ENVIRONMENT IS CRITICAL — read this or the test lies:**
.NET Framework 4.x is an *in-place* runtime; 4.5–4.8 share one CLR + GAC. An app *targeting*
net462 on a machine with **4.8 installed actually runs on 4.8**, so our net472 assemblies load
fine and the test **falsely passes**. To faithfully test a "4.6.2 customer," the box must have
**ONLY .NET Framework 4.6.2 installed (no 4.7.2+)**:
- Use a **Windows Server 2016** AMI (ships 4.6.2 by default). **Do NOT install a newer .NET Framework.**
- Windows Server 2019/2022 ship 4.8 → WRONG for this test (they're fine for Test 1).

1. Sanity (no profiler): `.\bin\Release\net462\NetFxV3Repro.exe` → prints `v3 bound`, app runs.
2. Install OUR local v4 build, register, run the **net462** exe:
   ```powershell
   Import-Module .\AWS.Otel.DotNet.Auto.psm1
   Install-OpenTelemetryCore -LocalPath "C:\path\to\aws-distro-...-windows.zip"
   Register-OpenTelemetryForCurrentSession -OTelServiceName "test2-net462"
   $env:OTEL_TRACES_EXPORTER="console"
   .\bin\Release\net462\NetFxV3Repro.exe
   ```
   **Classify:**
   - App starts and runs (SUMMARY prints), telemetry maybe absent → **the distro degraded
     gracefully on 4.6.2** (net472 assemblies didn't load, but app is fine).
   - App fails to start / throws `TypeLoadException`/`FileLoadException`/`BadImageFormatException`
     at startup → **the 4.6.2 drop breaks the app**. → must gate 4.6.2 customers.

---

## Fusion assembly-binding log (run once as Admin; shows which AWSSDK.Core binds)
```powershell
$f="HKLM:\SOFTWARE\Microsoft\Fusion"; New-Item $f -Force | Out-Null
Set-ItemProperty $f EnableLog 1; Set-ItemProperty $f ForceLog 1
Set-ItemProperty $f LogFailures 1; Set-ItemProperty $f LogResourceBinds 1
New-Item -ItemType Directory -Force -Path "$PWD\fusionlog" | Out-Null
Set-ItemProperty $f LogPath "$PWD\fusionlog\"
# TURN OFF after: Set-ItemProperty $f EnableLog 0
```

## Cleanup (always)
```powershell
Uninstall-OpenTelemetryCore
Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Fusion" EnableLog 0
```

## What to send back
Per test: full stdout, the loaded-AWSSDK.Core version lines, the OTel Managed self-log, and the
Fusion entry for `AWSSDK.Core`. That set answers break-vs-silent-loss for each cohort.
