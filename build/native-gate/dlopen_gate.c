// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0
//
// Load gate for the vendored native profiler.
//
// RTLD_NOW is the whole point: it forces every relocation to resolve at load time, which is what
// turns a missing source file or an unlinked library into a load FAILURE instead of a latent crash.
// `ldd` is not a substitute — it reports a library with undefined symbols as loadable.
//
// dlsym on the fork-only exports is the second half: a stock upstream binary opens fine and simply
// lacks AddLineProbes, and the managed side treats that as a normal runtime condition, so only an
// explicit symbol check can tell the two binaries apart.
#include <dlfcn.h>
#include <stdio.h>

int main(int argc, char** argv) {
    if (argc < 2) { printf("usage: dlopen_gate <library>\n"); return 2; }

    void* handle = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!handle) { printf("FAIL dlopen: %s\n", dlerror()); return 1; }

    const char* symbols[] = {"AddLineProbes", "RemoveLineProbe", "DllGetClassObject"};
    int rc = 0;
    for (int i = 0; i < 3; i++) {
        dlerror();
        void* sym = dlsym(handle, symbols[i]);
        if (!sym) { printf("FAIL dlsym %-18s: %s\n", symbols[i], dlerror()); rc = 1; }
        else      { printf("OK   dlsym %-18s -> %p\n", symbols[i], sym); }
    }
    return rc;
}
