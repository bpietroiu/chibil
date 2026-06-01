# SQLite SP3a — Cross-OS Disk Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give chibil-compiled SQLite a real on-disk database file on Windows and Linux/WSL — `open(sp3.db) → CREATE/INSERT → close → reopen → SELECT sum(a)` exits 55 from one `app.dll` — backed by native file I/O (libc on Linux, kernel32 on Windows) via chibil's P/Invoke.

**Architecture:** A custom cross-OS `sqlite3_vfs` (keep `SQLITE_OS_OTHER=1`) that does real disk I/O, dispatching to a Linux or Windows backend at runtime. This needs three new, independently-testable capabilities, each built and verified before the capstone: (1) a linker-synthesized `__chibil_os_is_windows()` intrinsic; (2) per-OS P/Invoke library routing (a linker `--pinvoke name=lib` map / `pinvokeMap` arg, so `open`→libc.so.6 and `CreateFileA`→kernel32.dll coexist, lazily resolved); (3) native struct ABI (`OVERLAPPED`) for atomic positioned I/O. No locking yet (SP3b). chibil codegen and the `.obj` format are untouched; all changes are linker-side + C in `samples/sqlite/`.

**Tech Stack:** C# / .NET 10, `System.Reflection.Metadata`; `tools/chibil-link/`; C (the SQLite VFS); xUnit; `DotnetHostRunner` (Windows) + `WslRunner` (Linux). WSL (Ubuntu + dotnet) is available on this machine, so the Linux legs genuinely run.

**Reference docs:** Spec `docs/superpowers/specs/2026-06-01-sqlite-managed-sp3a-design.md`.

**Key existing code (verified):**
- `tools/chibil-link/SymbolResolver.cs` — `Resolve(merger, objs, libs)` (`:33`); the MemberRef loop (`:56-95`) calls `SynthesizePInvoke` (`:98-127`) for unresolved externals; `SynthesizePInvoke` binds to `MapLib(libs[0])` with a multi-lib warning; `MapLib` (`:129-134`: `c`→`libc.so.6`, `m`→`libm.so.6`, dotted→as-is, else `lib<x>.so`). `merger.GetOrAddModuleRef(lib)` makes/dedups a ModuleRef. `merger.ReservePInvokeRow(stub)` reserves a P/Invoke MethodDef. `table.DefinedMethodToken` holds cross-object definitions.
- `tools/chibil-link/MetadataMerger.cs` — `SynthMethod {Name, SignatureBlob, Il, MaxStack, Attributes}`; `ReserveSynthRow(SynthMethod)` reserves a bodied synth MethodDef and returns its token; `GetOrAddCoreTypeRef(ns,name)` (added in SP2, private, returns `EntityHandle` TypeRef in mscorlib); `Builder` is the shared `MetadataBuilder`; `Md`/`Builder.GetOrAddBlob`/`AddMemberReference` available.
- `tools/chibil-link/PeWriter.cs` — `LinkPipeline.LinkToBytes(objs, libs, exportClass=null)` (`:17`); `PeWriter` ctor `(objs, libs, exportClass)`; `Write()` calls `SymbolResolver.Resolve(merger, _objs, _libs)` (`:57`). The synth body-emission loop already handles `slot.Synth` bodies.
- `tools/chibil-link/Program.cs` — `LinkOptions {Inputs, Libraries, Output, ExportClass}`; `Parse` handles `-o`, `-l`, `--export-class=`; `Linker.Run` calls `LinkToBytes(objs, opts.Libraries, opts.ExportClass)`.
- `samples/sqlite/sqlite_shim.c` — provides mem/str + heap + the `:memory:` stub VFS via `sqlite3_os_init` (registers `g_vfs`). **Leave untouched.** `platform_init()` configures the heap + calls `sqlite3_initialize()`.
- `samples/sqlite/main.c` — the existing `:memory:` harness (untouched).
- `samples/sqlite/include/` — minimal libc headers (`stddef.h`, `stdlib.h`, `string.h`, `time.h`, …); chibil-parseable.
- Test runners: `DotnetHostRunner.RunPeViaDotnetHost(pe, out stdout)` (Windows subprocess; creates a temp dir, writes `app.dll`+runtimeconfig, runs `dotnet app.dll`), `RunDllInDir`, `DotnetAvailable()`, `RuntimeConfigJson`; `WslRunner.Run(pe, NetCoreRuntimeConfig)` (runs in a `/mnt/...` temp dir via `cd '<dir>' && dotnet app.dll`), `RunDirEntry`, `Available()`. `TestCompiler.CompileToObj(src, target)` / `CompileFileToObj(path, target, defs, incs)`; `ObjectFile.Load`; `LinkPipeline.LinkToBytes`.

**New signatures used across tasks (define once, reuse):**
- `LinkPipeline.LinkToBytes(IReadOnlyList<ObjectFile> objs, List<string> libs, string exportClass = null, Dictionary<string,string> pinvokeMap = null)`
- `SymbolResolver.Resolve(MetadataMerger merger, IReadOnlyList<ObjectFile> objs, List<string> libs, Dictionary<string,string> pinvokeMap)`
- `MetadataMerger.ReserveOsIsWindowsIntrinsic()` → `int` token (deduped)
- `pinvokeMap`: symbol name → library token (passed through `MapLib`): e.g. `{"open","c"}`, `{"CreateFileA","kernel32"}`.

---

## Task 1: The `__chibil_os_is_windows()` intrinsic

A linker-synthesized C-callable function returning 1 on Windows, 0 elsewhere — the runtime OS switch the VFS uses.

**Files:** Modify `tools/chibil-link/MetadataMerger.cs`, `tools/chibil-link/SymbolResolver.cs`; Create `tests/Chibil.Tests/CoreClr/OsDispatchTests.cs`.

- [ ] **Step 1: Write the failing test** (`tests/Chibil.Tests/CoreClr/OsDispatchTests.cs`)

```csharp
using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class OsDispatchTests
{
    static byte[] Link(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
    }

    const string Src = "int __chibil_os_is_windows(void); " +
                       "int main(void){ return __chibil_os_is_windows() ? 55 : 44; }";

    [Fact]
    public void Os_is_windows_true_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DotnetHostRunner.RunPeViaDotnetHost(Link(Src), out string o);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Os_is_windows_false_on_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(Src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 44, $"linux expected 44, got {exit}. {o}");
    }
}
```

- [ ] **Step 2: Run it — expect FAIL** (`__chibil_os_is_windows` is unresolved → `SynthesizePInvoke` throws "no -l libraries given")

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter OsDispatchTests`
Expected: FAIL (link throws, or non-55/44 exit).

- [ ] **Step 3: Add `ReserveOsIsWindowsIntrinsic` to the merger** (`MetadataMerger.cs`)

```csharp
    private int _osIsWindowsToken;

    /// <summary>Reserve a C-callable intrinsic `int __chibil_os_is_windows()` whose
    /// body is `call bool [mscorlib]System.OperatingSystem::IsWindows(); ret` (the
    /// bool result is the i4 the C `int` ABI expects). Deduped. Returns its MethodDef
    /// token. Used by the cross-OS disk VFS to pick its backend at runtime.</summary>
    public int ReserveOsIsWindowsIntrinsic()
    {
        if (_osIsWindowsToken != 0) return _osIsWindowsToken;

        // MemberRef: System.OperatingSystem::IsWindows() : bool   (static)
        var boolSig = new BlobBuilder();
        new BlobEncoder(boolSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().Boolean(), _ => { });
        var osType = GetOrAddCoreTypeRef("System", "OperatingSystem");
        var isWin = Builder.AddMemberReference(osType, Builder.GetOrAddString("IsWindows"),
            Builder.GetOrAddBlob(boolSig));

        // Intrinsic signature: int32 __chibil_os_is_windows()   (static)
        var mSig = new BlobBuilder();
        new BlobEncoder(mSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().Int32(), _ => { });

        var il = new BlobBuilder();
        il.WriteByte(0x28); il.WriteInt32(MetadataTokens.GetToken(isWin)); // call IsWindows
        il.WriteByte(0x2A);                                                // ret
        var synth = new SynthMethod
        {
            Name = "__chibil_os_is_windows",
            SignatureBlob = Builder.GetOrAddBlob(mSig),
            Il = il.ToArray(),
            MaxStack = 1,
            Attributes = System.Reflection.MethodAttributes.Public
                       | System.Reflection.MethodAttributes.Static
                       | System.Reflection.MethodAttributes.HideBySig,
        };
        _osIsWindowsToken = ReserveSynthRow(synth);
        return _osIsWindowsToken;
    }
```
> `GetOrAddCoreTypeRef` is the SP2 helper (private, in this class). `BlobEncoder`/`SignatureCallingConvention`/`MethodAttributes`/`MetadataTokens` are already used in this file. Confirm `ret.Type().Boolean()` exists on the return-type encoder (it does in `System.Reflection.Metadata.Ecma335`); if the fluent shape differs, match how other signatures in this file encode primitives.

- [ ] **Step 4: Branch to the intrinsic in `SymbolResolver`** (`SymbolResolver.cs`)

In `Resolve`, inside the MemberRef loop, BEFORE the `table.DefinedMethodToken.TryGetValue` check (so a stray same-named definition can't shadow it — there won't be one, but order it first), add:
```csharp
                if (name == "__chibil_os_is_windows")
                {
                    map.RecordExternal(originalToken, merger.ReserveOsIsWindowsIntrinsic());
                    continue;
                }
```
(`map` and `originalToken` are already in scope in that loop.)

- [ ] **Step 5: Run the test — expect PASS on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter OsDispatchTests`
Expected: `Os_is_windows_true_on_windows` → 55; `Os_is_windows_false_on_linux` → 44. If the intrinsic fails to load (a `TypeLoadException`/`MissingMethodException` in the host output about `OperatingSystem`), the mscorlib facade may not forward `System.OperatingSystem` — report it; fallback would be `System.Runtime.InteropServices.RuntimeInformation::IsOSPlatform`, but try `OperatingSystem` first.

- [ ] **Step 6: Confirm no regression**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: 0 failures (the intrinsic is only synthesized when the name appears; existing links are unaffected).

- [ ] **Step 7: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/OsDispatchTests.cs
git commit -m "feat(linker): __chibil_os_is_windows intrinsic (runtime OS switch for C)"
```

---

## Task 2: Per-OS P/Invoke library routing

Route each native symbol to its own module (`open`→libc.so.6, `CreateFileA`→kernel32.dll) in one binary, via a `pinvokeMap`.

**Files:** Modify `tools/chibil-link/PeWriter.cs` (`LinkToBytes`, ctor), `tools/chibil-link/SymbolResolver.cs`, `tools/chibil-link/Program.cs`; Create `tests/Chibil.Tests/CoreClr/PinvokeRoutingTests.cs`.

- [ ] **Step 1: Write the failing test** (`tests/Chibil.Tests/CoreClr/PinvokeRoutingTests.cs`)

```csharp
using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PinvokeRoutingTests
{
    // Calls a libc fn on Linux and a kernel32 fn on Windows, gated on the OS flag.
    // Both return a positive process id → map to 55. Proves per-symbol routing AND
    // lazy resolution: the wrong-OS stub is present but never called, so it never loads.
    const string Src =
        "int __chibil_os_is_windows(void); " +
        "int getpid(void); " +                    // libc
        "unsigned int GetCurrentProcessId(void); " + // kernel32
        "int main(void){ int id = __chibil_os_is_windows() ? (int)GetCurrentProcessId() : getpid();" +
        " return id > 0 ? 55 : 44; }";

    static byte[] Link(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        var map = new Dictionary<string, string> { ["getpid"] = "c", ["GetCurrentProcessId"] = "kernel32" };
        return LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), null, map);
    }

    [Fact]
    public void Routing_windows_kernel32()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DotnetHostRunner.RunPeViaDotnetHost(Link(Src), out string o);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Routing_linux_libc()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(Src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux expected 55, got {exit}. {o}");
    }
}
```

- [ ] **Step 2: Run it — expect FAIL** (no 4-arg `LinkToBytes` overload → compile error)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter PinvokeRoutingTests`
Expected: FAIL (does not compile).

- [ ] **Step 3: Thread `pinvokeMap` through the pipeline** (`PeWriter.cs`)

Change `LinkToBytes`:
```csharp
    public static byte[] LinkToBytes(IReadOnlyList<ObjectFile> objs, List<string> libs,
        string exportClass = null, Dictionary<string, string> pinvokeMap = null)
    {
        if (objs == null || objs.Count == 0)
            throw new LinkException("no input objects.");
        return new PeWriter(objs, libs ?? new List<string>(), exportClass, pinvokeMap).Write();
    }
```
Add the field + ctor param to `PeWriter` (it has `_objs`, `_libs`, `_exportClass`):
```csharp
    private readonly Dictionary<string, string> _pinvokeMap;

    public PeWriter(IReadOnlyList<ObjectFile> objs, List<string> libs, string exportClass = null,
        Dictionary<string, string> pinvokeMap = null)
    {
        _objs = objs;
        _libs = libs;
        _exportClass = ValidateExportClass(exportClass);
        _pinvokeMap = pinvokeMap ?? new Dictionary<string, string>();
    }
```
In `Write()`, change the resolver call from `SymbolResolver.Resolve(merger, _objs, _libs);` to:
```csharp
        SymbolResolver.Resolve(merger, _objs, _libs, _pinvokeMap);
```
> Ensure `using System.Collections.Generic;` is present (it is).

- [ ] **Step 4: Use the map in `SymbolResolver`** (`SymbolResolver.cs`)

Change `Resolve`'s signature to take the map and pass it down:
```csharp
    public static void Resolve(MetadataMerger merger, IReadOnlyList<ObjectFile> objs,
        List<string> libs, Dictionary<string, string> pinvokeMap)
```
At the `SynthesizePInvoke` call site (`:90`), pass the map:
```csharp
                    pinvokeToken = SynthesizePInvoke(merger, of, name, sigReader, libs, pinvokeMap);
```
Change `SynthesizePInvoke` to consult the map first:
```csharp
    private static int SynthesizePInvoke(
        MetadataMerger merger, ObjectFile of, string name, BlobReader signatureBlobReader,
        List<string> libs, Dictionary<string, string> pinvokeMap)
    {
        string lib;
        if (pinvokeMap.TryGetValue(name, out string libTok))
        {
            lib = MapLib(libTok);                    // explicit per-symbol routing
        }
        else if (libs.Count > 0)
        {
            lib = MapLib(libs[0]);
            if (libs.Count > 1)
                Console.Error.WriteLine(
                    $"chibil-link: warning: '{name}' bound to '{lib}' (first -l library); " +
                    $"add it to --pinvoke for explicit routing.");
        }
        else
        {
            throw new LinkException(
                $"unresolved symbol '{name}' and no -l libraries or --pinvoke mapping given");
        }
        var moduleRef = merger.GetOrAddModuleRef(lib);

        var sigB = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(signatureBlobReader, merger.MapFor(of), sigB);
        var sigBlob = merger.Md.GetOrAddBlob(sigB);

        var stub = new MetadataMerger.PInvokeStub { Name = name, SignatureBlob = sigBlob, ModuleRef = moduleRef };
        return merger.ReservePInvokeRow(stub);
    }
```
Extend `MapLib` to know Windows modules:
```csharp
    private static string MapLib(string l) => l switch
    {
        "c" => "libc.so.6",
        "m" => "libm.so.6",
        "kernel32" => "kernel32.dll",
        _ => l.Contains('.') ? l : $"lib{l}.so",
    };
```

- [ ] **Step 5: Add `--pinvoke` to the CLI** (`Program.cs`)

In `LinkOptions` (after `ExportClass`):
```csharp
    public Dictionary<string, string> PinvokeMap = new();   // symbol -> library token
```
In `Parse`, before the generic `-` rejection:
```csharp
            if (a.StartsWith("--pinvoke="))
            {
                foreach (var pair in a["--pinvoke=".Length..].Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) { System.Console.Error.WriteLine($"bad --pinvoke entry: {pair}"); return null; }
                    o.PinvokeMap[pair[..eq]] = pair[(eq + 1)..];
                }
                continue;
            }
```
In `Linker.Run`, pass it:
```csharp
        byte[] pe = LinkPipeline.LinkToBytes(objs, opts.Libraries, opts.ExportClass, opts.PinvokeMap);
```
> `LinkOptions` needs `using System.Collections.Generic;` at the top of `Program.cs` (add if absent).

- [ ] **Step 6: Run the test — expect PASS on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter PinvokeRoutingTests`
Expected: both → 55. If Linux fails with a `DllNotFoundException` for `kernel32.dll`, the wrong-OS stub was *called* (OS dispatch bug) or eagerly loaded — confirm the `__chibil_os_is_windows()` branch guards it and that .NET resolves DllImports lazily (it does; an uncalled stub never loads). If Windows fails on `libc.so.6`, same in reverse.

- [ ] **Step 7: Full suite**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: 0 failures (existing tests pass `null` map → unchanged behavior).

- [ ] **Step 8: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/PinvokeRoutingTests.cs
git commit -m "feat(linker): per-OS P/Invoke library routing (--pinvoke map)"
```

---

## Task 3: Native headers + positioned-I/O round-trip

Build the chibil-parseable POSIX/Win32 decls (incl. `OVERLAPPED`) and prove atomic positioned read/write works on both OSes — the native-ABI oracle the VFS depends on.

**Files:** Create `samples/sqlite/include/chibil_os.h` (the cross-OS native decls); Create `tests/Chibil.Tests/CoreClr/PositionedIoTests.cs`.

- [ ] **Step 1: Create `samples/sqlite/include/chibil_os.h`** — the cross-OS native surface

```c
/* samples/sqlite/include/chibil_os.h
 * Cross-OS native file I/O decls for the chibil disk VFS. The linker routes each
 * symbol to libc.so.6 (Linux) or kernel32.dll (Windows) via --pinvoke; the C picks
 * the right one at runtime via __chibil_os_is_windows(). x64 only. */
#ifndef CHIBIL_OS_H
#define CHIBIL_OS_H

extern int __chibil_os_is_windows(void);

/* ---- POSIX (libc) ---- */
#define O_RDONLY 0
#define O_WRONLY 1
#define O_RDWR   2
#define O_CREAT  0100   /* octal 0100 = 64 (Linux x86-64) */
#define O_TRUNC  01000  /* octal 01000 = 512 */
#define SEEK_SET 0
#define SEEK_END 2
#define F_OK     0
extern int  open(const char *path, int flags, int mode);   /* declared non-variadic: always pass mode */
extern long pread(int fd, void *buf, unsigned long n, long long off);
extern long pwrite(int fd, const void *buf, unsigned long n, long long off);
extern int  ftruncate(int fd, long long len);
extern int  fsync(int fd);
extern int  close(int fd);
extern int  unlink(const char *path);
extern int  access(const char *path, int mode);
extern long long lseek(int fd, long long off, int whence);

/* ---- Win32 (kernel32) ---- */
#define GENERIC_READ          0x80000000u
#define GENERIC_WRITE         0x40000000u
#define FILE_SHARE_READ       0x00000001u
#define FILE_SHARE_WRITE      0x00000002u
#define CREATE_NEW            1
#define OPEN_EXISTING         3
#define OPEN_ALWAYS           4
#define FILE_ATTRIBUTE_NORMAL 0x80u
#define FILE_BEGIN            0
#define INVALID_FILE_ATTRIBUTES 0xFFFFFFFFu

/* OVERLAPPED, x64 layout (32 bytes). We set Offset/OffsetHigh from a 64-bit offset. */
typedef struct OVERLAPPED {
    unsigned long long Internal;
    unsigned long long InternalHigh;
    unsigned int Offset;
    unsigned int OffsetHigh;
    void *hEvent;
} OVERLAPPED;

extern void *CreateFileA(const char *name, unsigned int access, unsigned int share,
                         void *sec, unsigned int disposition, unsigned int flags, void *templ);
extern int  ReadFile(void *h, void *buf, unsigned int n, unsigned int *nread, OVERLAPPED *ov);
extern int  WriteFile(void *h, const void *buf, unsigned int n, unsigned int *nwrote, OVERLAPPED *ov);
extern int  SetFilePointerEx(void *h, long long dist, long long *newPos, unsigned int method);
extern int  SetEndOfFile(void *h);
extern int  FlushFileBuffers(void *h);
extern int  CloseHandle(void *h);
extern int  DeleteFileA(const char *name);
extern int  GetFileSizeEx(void *h, long long *size);
extern unsigned int GetFileAttributesA(const char *name);

/* INVALID_HANDLE_VALUE == (void*)-1 */
#define INVALID_HANDLE_VALUE ((void *)(long long)-1)

#endif
```
> The `--pinvoke` map for these symbols (used by every later link) is:
> `open=c,pread=c,pwrite=c,ftruncate=c,fsync=c,close=c,unlink=c,access=c,lseek=c,CreateFileA=kernel32,ReadFile=kernel32,WriteFile=kernel32,SetFilePointerEx=kernel32,SetEndOfFile=kernel32,FlushFileBuffers=kernel32,CloseHandle=kernel32,DeleteFileA=kernel32,GetFileSizeEx=kernel32,GetFileAttributesA=kernel32`

- [ ] **Step 2: Write the failing test** (`tests/Chibil.Tests/CoreClr/PositionedIoTests.cs`)

The test compiles a tiny C program that opens a temp file, writes 4 bytes at offset 8, reads them back at offset 8, and returns 55 on match. It is OS-dispatched internally. Place the program inline as a source string and compile it with the include dir.

```csharp
using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PositionedIoTests
{
    static readonly string[] Map =
    {
        "open=c","pread=c","pwrite=c","ftruncate=c","fsync=c","close=c","unlink=c","access=c","lseek=c",
        "CreateFileA=kernel32","ReadFile=kernel32","WriteFile=kernel32","SetFilePointerEx=kernel32",
        "SetEndOfFile=kernel32","FlushFileBuffers=kernel32","CloseHandle=kernel32","DeleteFileA=kernel32",
        "GetFileSizeEx=kernel32","GetFileAttributesA=kernel32",
    };
    internal static Dictionary<string,string> PinvokeMap()
    {
        var d = new Dictionary<string,string>();
        foreach (var e in Map) { int i = e.IndexOf('='); d[e[..i]] = e[(i+1)..]; }
        return d;
    }

    // Writes 0x41424344 at offset 8 to "pio.tmp", reads it back, returns 55 on match.
    const string Src = @"
#include ""chibil_os.h""
typedef unsigned long long u64;
static long w_open(const char* p){
    if (__chibil_os_is_windows())
        return (long long)(void*)CreateFileA(p, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    return open(p, O_RDWR|O_CREAT|O_TRUNC, 420);
}
static int w_pwrite(long h, const void* b, unsigned int n, long long off){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; char* z=(char*)&ov; for(int i=0;i<(int)sizeof ov;i++) z[i]=0; ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32); unsigned int wr=0; return WriteFile((void*)h,b,n,&wr,&ov)&&wr==n?0:-1; }
    return pwrite((int)h,b,n,off)==(long)n?0:-1;
}
static int w_pread(long h, void* b, unsigned int n, long long off){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; char* z=(char*)&ov; for(int i=0;i<(int)sizeof ov;i++) z[i]=0; ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32); unsigned int rd=0; return ReadFile((void*)h,b,n,&rd,&ov)&&rd==n?0:-1; }
    return pread((int)h,b,n,off)==(long)n?0:-1;
}
static void w_close(long h){ if(__chibil_os_is_windows()) CloseHandle((void*)h); else close((int)h); }
int main(void){
    long h = w_open(""pio.tmp"");
    unsigned int v = 0x41424344u, r = 0;
    if (w_pwrite(h, &v, 4, 8) != 0) return 1;
    if (w_pread (h, &r, 4, 8) != 0) return 2;
    w_close(h);
    return r == 0x41424344u ? 55 : 44;
}";

    static byte[] Link()
    {
        string sq = SqliteSmokeTests.RepoRootDir();          // see note below
        string inc = Path.Combine(sq, "samples", "sqlite", "include");
        // Materialize Src to a temp .c so the include path resolves "chibil_os.h".
        string dir = Path.Combine(Path.GetTempPath(), "chibil_pio_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string c = Path.Combine(dir, "pio.c");
            File.WriteAllText(c, Src);
            byte[] obj = TestCompiler.CompileFileToObj(c, Chibil.TargetProfile.CoreClr, null, new[] { inc });
            var of = ObjectFile.Load(obj, "pio.obj");
            return LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), null, PinvokeMap());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Positioned_io_roundtrip_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DiskRunner.RunWindows(Link(), out string o, out _);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Positioned_io_roundtrip_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux expected 55, got {exit}. {o}");
    }
}
```
> Two helpers this references must exist:
> 1. `SqliteSmokeTests.RepoRootDir()` — add this exact wrapper to `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs` returning the repo root: `internal static string RepoRootDir() => RepoRoot();`. (If `RepoRoot()` is `private`, also change it to `internal static` so the wrapper compiles. `RepoRoot()` already walks up to the dir containing `samples/sqlite`, i.e. the repo root.)
> 2. `DiskRunner.RunWindows` (Task 3 Step 3).
>
> On Linux `WslRunner.Run` runs `dotnet app.dll` with cwd = the staged `/mnt/...` dir, so `pio.tmp`/`sp3.db` are created there. On Windows the default `dotnet` child cwd is NOT the app dir, so a relative file would escape it — hence `DiskRunner.RunWindows` sets the child working directory.

- [ ] **Step 3: Add `DiskRunner` (Windows cwd-aware host)** (`tests/Chibil.Tests/CoreClr/DiskRunner.cs`)

```csharp
using System;
using System.Diagnostics;
using System.IO;

namespace Chibil.Tests.CoreClr;

/// <summary>Runs a produced PE under the dotnet host with the child WORKING
/// DIRECTORY set to the temp dir, so a C program that opens a relative file
/// (sp3.db) creates it there. Optionally reports a probed file's size before
/// cleanup (to assert on-disk persistence).</summary>
internal static class DiskRunner
{
    public static int RunWindows(byte[] pe, out string stdout, out long probeSize, string probeFile = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_disk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dll = Path.Combine(dir, "app.dll");
            File.WriteAllBytes(dll, pe);
            File.WriteAllText(Path.Combine(dir, "app.runtimeconfig.json"), DotnetHostRunner.RuntimeConfigJson);
            using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir });
            stdout = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("dotnet host timed out"); }
            if (err.Length > 0) stdout += "\n[stderr] " + err;
            probeSize = probeFile != null && File.Exists(Path.Combine(dir, probeFile))
                ? new FileInfo(Path.Combine(dir, probeFile)).Length : -1;
            return p.ExitCode;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
```

- [ ] **Step 4: Run the test — expect PASS on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter PositionedIoTests`
Expected: both → 55. If Windows returns 1/2 (write/read failed), inspect the `OVERLAPPED` layout/zeroing or the `CreateFileA` flags; if it returns 44 (mismatch), the offset handling is wrong. If Linux returns 1/2, check `pread`/`pwrite` signatures (off_t is the 4th arg, 64-bit) and the `--pinvoke` map.

- [ ] **Step 5: Commit**

```bash
git add samples/sqlite/include/chibil_os.h tests/Chibil.Tests/CoreClr/PositionedIoTests.cs tests/Chibil.Tests/CoreClr/DiskRunner.cs tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs
git commit -m "feat(sqlite): cross-OS native file I/O decls + positioned-I/O round-trip (both OSes)"
```
> The `SqliteSmokeTests.cs` change is only exposing `RepoRoot()` as `internal static` (rename to `RepoRootDir()` or add a thin internal wrapper) — keep its existing behavior.

---

## Task 4: The disk VFS + on-disk SQLite (capstone)

**Files:** Create `samples/sqlite/sqlite_vfs_disk.c`, `samples/sqlite/main_disk.c`; Create `tests/Chibil.Tests/CoreClr/SqliteDiskTests.cs`.

- [ ] **Step 1: Create `samples/sqlite/sqlite_vfs_disk.c`** — the cross-OS disk VFS

```c
/* samples/sqlite/sqlite_vfs_disk.c — a real on-disk sqlite3_vfs, cross-OS.
 * File I/O goes to libc (Linux) or kernel32 (Windows), picked at runtime via
 * __chibil_os_is_windows(). Single connection, NO locking (xLock/xUnlock no-op);
 * the SQLite lock protocol is SP3b. Register with register_disk_vfs(). */
#include "sqlite3.h"
#include "chibil_os.h"

static int g_win;

typedef struct DiskFile { sqlite3_file base; long long h; } DiskFile;

static void zero(void *p, int n){ char *z=(char*)p; for(int i=0;i<n;i++) z[i]=0; }

static int dfRead(sqlite3_file *f, void *buf, int n, sqlite3_int64 off){
    DiskFile *df=(DiskFile*)f; long got;
    if (g_win){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        unsigned int rd=0; ReadFile((void*)df->h,buf,(unsigned int)n,&rd,&ov); got=(long)rd; }
    else got = pread((int)df->h, buf, (unsigned long)n, off);
    if (got == n) return SQLITE_OK;
    if (got < 0) return SQLITE_IOERR_READ;
    char *z=(char*)buf; for (long i=got;i<n;i++) z[i]=0;   /* zero-fill tail */
    return SQLITE_IOERR_SHORT_READ;
}
static int dfWrite(sqlite3_file *f, const void *buf, int n, sqlite3_int64 off){
    DiskFile *df=(DiskFile*)f; long put;
    if (g_win){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        unsigned int wr=0; WriteFile((void*)df->h,buf,(unsigned int)n,&wr,&ov); put=(long)wr; }
    else put = pwrite((int)df->h, buf, (unsigned long)n, off);
    return put == n ? SQLITE_OK : SQLITE_IOERR_WRITE;
}
static int dfTruncate(sqlite3_file *f, sqlite3_int64 size){
    DiskFile *df=(DiskFile*)f;
    if (g_win){ long long np; if(!SetFilePointerEx((void*)df->h,size,&np,FILE_BEGIN)) return SQLITE_IOERR_TRUNCATE;
        return SetEndOfFile((void*)df->h) ? SQLITE_OK : SQLITE_IOERR_TRUNCATE; }
    return ftruncate((int)df->h, size)==0 ? SQLITE_OK : SQLITE_IOERR_TRUNCATE;
}
static int dfSync(sqlite3_file *f, int flags){ (void)flags; DiskFile *df=(DiskFile*)f;
    if (g_win) return FlushFileBuffers((void*)df->h) ? SQLITE_OK : SQLITE_IOERR_FSYNC;
    return fsync((int)df->h)==0 ? SQLITE_OK : SQLITE_IOERR_FSYNC; }
static int dfFileSize(sqlite3_file *f, sqlite3_int64 *pSize){ DiskFile *df=(DiskFile*)f;
    if (g_win){ long long s=0; if(!GetFileSizeEx((void*)df->h,&s)) return SQLITE_IOERR_FSTAT; *pSize=s; return SQLITE_OK; }
    long long s = lseek((int)df->h, 0, SEEK_END); if (s<0) return SQLITE_IOERR_FSTAT; *pSize=s; return SQLITE_OK; }
static int dfClose(sqlite3_file *f){ DiskFile *df=(DiskFile*)f;
    if (g_win) CloseHandle((void*)df->h); else close((int)df->h); return SQLITE_OK; }
static int dfLock(sqlite3_file *f, int e){ (void)f;(void)e; return SQLITE_OK; }      /* SP3b */
static int dfUnlock(sqlite3_file *f, int e){ (void)f;(void)e; return SQLITE_OK; }
static int dfCheckLock(sqlite3_file *f, int *p){ (void)f; *p=0; return SQLITE_OK; }
static int dfControl(sqlite3_file *f, int op, void *a){ (void)f;(void)op;(void)a; return SQLITE_NOTFOUND; }
static int dfSectorSize(sqlite3_file *f){ (void)f; return 512; }
static int dfDevChar(sqlite3_file *f){ (void)f; return 0; }

static sqlite3_io_methods g_io = {
    1, dfClose, dfRead, dfWrite, dfTruncate, dfSync, dfFileSize,
    dfLock, dfUnlock, dfCheckLock, dfControl, dfSectorSize, dfDevChar
};

static int vOpen(sqlite3_vfs *v, const char *z, sqlite3_file *f, int flags, int *pOut){
    (void)v; DiskFile *df=(DiskFile*)f; df->base.pMethods=0;
    long long h;
    if (g_win){
        unsigned int disp = (flags & SQLITE_OPEN_CREATE) ? OPEN_ALWAYS : OPEN_EXISTING;
        h = (long long)(void*)CreateFileA(z, GENERIC_READ|GENERIC_WRITE,
                FILE_SHARE_READ|FILE_SHARE_WRITE, 0, disp, FILE_ATTRIBUTE_NORMAL, 0);
        if ((void*)h == INVALID_HANDLE_VALUE) return SQLITE_CANTOPEN;
    } else {
        int of = O_RDWR | ((flags & SQLITE_OPEN_CREATE) ? O_CREAT : 0);
        h = open(z, of, 420);
        if (h < 0) return SQLITE_CANTOPEN;
    }
    df->h = h; df->base.pMethods = &g_io;
    if (pOut) *pOut = flags & (SQLITE_OPEN_READONLY|SQLITE_OPEN_READWRITE|SQLITE_OPEN_CREATE);
    return SQLITE_OK;
}
static int vDelete(sqlite3_vfs *v, const char *z, int s){ (void)v;(void)s;
    if (g_win) return DeleteFileA(z) ? SQLITE_OK : SQLITE_IOERR_DELETE;
    return unlink(z)==0 ? SQLITE_OK : SQLITE_IOERR_DELETE; }
static int vAccess(sqlite3_vfs *v, const char *z, int flags, int *pOut){ (void)v;(void)flags;
    if (g_win) *pOut = (GetFileAttributesA(z) != INVALID_FILE_ATTRIBUTES);
    else *pOut = (access(z, F_OK) == 0);
    return SQLITE_OK; }
static int vFullPath(sqlite3_vfs *v, const char *z, int n, char *out){ (void)v;
    int i=0; while(z[i] && i<n-1){ out[i]=z[i]; i++; } out[i]=0; return SQLITE_OK; }

/* VFS-level housekeeping (own copies; sqlite_shim.c's are static there). */
static unsigned int g_rng = 0xC0FFEEu;
static int vRand(sqlite3_vfs *v,int n,char *o){ (void)v; for(int i=0;i<n;i++){ g_rng=g_rng*1103515245u+12345u; o[i]=(char)(g_rng>>16);} return n; }
static int vSleep(sqlite3_vfs *v,int us){ (void)v;(void)us; return 0; }
static int vCurTime(sqlite3_vfs *v,double *p){ (void)v; *p=2440587.5; return SQLITE_OK; }
static int vLastErr(sqlite3_vfs *v,int n,char *b){ (void)v;(void)n;(void)b; return 0; }

static sqlite3_vfs g_disk_vfs = {
    3, sizeof(DiskFile), 1024, 0, "chibil-disk", 0,
    vOpen, vDelete, vAccess, vFullPath,
    0,0,0,0,
    vRand, vSleep, vCurTime, vLastErr,
    0, 0,0,0
};

void register_disk_vfs(void){
    g_win = __chibil_os_is_windows();
    sqlite3_vfs_register(&g_disk_vfs, 1);   /* makeDflt = 1 */
}
```
> The `sqlite3_io_methods`/`sqlite3_vfs` field order must match `vendor/sqlite3.h` (iVersion 3, same trailing-zero shape as `sqlite_shim.c`'s `g_vfs`). `SQLITE_OPEN_*` / `SQLITE_IOERR_*` come from `sqlite3.h`. `sqlite3_int64` is SQLite's 64-bit type.

- [ ] **Step 2: Create `samples/sqlite/main_disk.c`** — the persistence harness

```c
/* samples/sqlite/main_disk.c — on-disk CRUD: write, close, REOPEN, read back.
 * Returns SELECT sum(a) = 55, proving the data round-tripped through sp3.db. */
#include "sqlite3.h"
void platform_init(void);
void register_disk_vfs(void);

static int run(void){
    sqlite3 *db = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 101;
    if (sqlite3_exec(db, "CREATE TABLE IF NOT EXISTS t(a INTEGER);"
                         "DELETE FROM t;"
                         "INSERT INTO t VALUES(20),(22),(13);", 0,0,0) != SQLITE_OK) return 102;
    sqlite3_close(db);                       /* flush + close the file */
    return 0;
}
static int readback(void){
    sqlite3 *db = 0; int sum = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 103;   /* fresh connection */
    sqlite3_stmt *st = 0;
    if (sqlite3_prepare_v2(db, "SELECT sum(a) FROM t", -1, &st, 0) != SQLITE_OK) return 104;
    if (sqlite3_step(st) == SQLITE_ROW) sum = sqlite3_column_int(st, 0);
    sqlite3_finalize(st);
    sqlite3_close(db);
    return sum;
}
int main(void){
    platform_init();
    register_disk_vfs();
    int rc = run(); if (rc) return rc;
    return readback();   /* expect 55 */
}
```

- [ ] **Step 3: Write the capstone test** (`tests/Chibil.Tests/CoreClr/SqliteDiskTests.cs`)

```csharp
using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class SqliteDiskTests
{
    static byte[] BuildDiskAppDll()
    {
        string sq = Path.Combine(SqliteSmokeTests.RepoRootDir(), "samples", "sqlite");
        string[] defs = { "SQLITE_OS_OTHER=1", "SQLITE_THREADSAFE=0", "SQLITE_TEMP_STORE=3",
            "SQLITE_ENABLE_MEMSYS5=1", "SQLITE_ZERO_MALLOC=1", "SQLITE_OMIT_LOADEXTENSION=1", "SQLITE_OMIT_AUTOINIT=1" };
        string[] incs = { Path.Combine(sq, "include"), Path.Combine(sq, "vendor") };
        var objs = new List<ObjectFile>();
        foreach (var src in new[]
        {
            Path.Combine(sq, "vendor", "sqlite3.c"),
            Path.Combine(sq, "sqlite_shim.c"),
            Path.Combine(sq, "sqlite_vfs_disk.c"),
            Path.Combine(sq, "main_disk.c"),
        })
        {
            byte[] obj = TestCompiler.CompileFileToObj(src, Chibil.TargetProfile.CoreClr, defs, incs);
            objs.Add(ObjectFile.Load(obj, Path.GetFileName(src)));
        }
        return LinkPipeline.LinkToBytes(objs, new List<string>(), null, PositionedIoTests.PinvokeMap());
    }

    [Fact]
    public void Disk_db_crud_returns_55_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DiskRunner.RunWindows(BuildDiskAppDll(), out string o, out long size, "sp3.db");
        Assert.True(exit == 55, $"windows exit {exit}: {o}");
        Assert.True(size > 0, $"sp3.db not created on disk (size {size})");
    }

    [Fact]
    public void Disk_db_crud_returns_55_on_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(BuildDiskAppDll(), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux exit {exit}: {o}");
    }
}
```

- [ ] **Step 4: Run the capstone — expect 55 on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter SqliteDiskTests`
Expected: both → exit 55; Windows also asserts `sp3.db` size > 0. (~40s sqlite3.c compile.) Triage (report loudly, don't mask):
- `101`/`103` → open failed (CreateFileA/open flags or path/cwd — confirm `DiskRunner` sets `WorkingDirectory`; WSL runs with cwd = staged dir).
- `102` → exec failed (a file-method bug; check write/sync/filesize).
- `104` or non-55 sum → read-back failed: data didn't persist — check `dfWrite`/`dfSync`/`dfRead` and that `sqlite3_close` flushed before reopen.
- AccessViolation → a forwarder/struct-ABI fault; check `OVERLAPPED` and the `sqlite3_io_methods` field order.

- [ ] **Step 5: Confirm `:memory:` + SP2 unaffected**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "SqliteSmokeTests|SqliteExportTests"`
Expected: all green (the shim and export path are untouched; the disk VFS is a separate file/harness).

- [ ] **Step 6: Commit**

```bash
git add samples/sqlite/ tests/Chibil.Tests/CoreClr/SqliteDiskTests.cs
git commit -m "feat(sqlite): on-disk persistence via cross-OS disk VFS (sp3.db, 55 on Windows+Linux)"
```

---

## Task 5: Build script, regression gate, docs

**Files:** Create `samples/sqlite/build-chibil-disk.sh`; Modify `samples/sqlite/README.md`.

- [ ] **Step 1: Add a disk build script** (`samples/sqlite/build-chibil-disk.sh`)

Mirror `build-chibil.sh` but compile the 4-file disk set and pass the `--pinvoke` map to the link step:
```bash
#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/app-disk.dll}"
CH="$ROOT/chibil"; LINK="$ROOT/tools/chibil-link"
DEFS=(-DSQLITE_OS_OTHER=1 -DSQLITE_THREADSAFE=0 -DSQLITE_TEMP_STORE=3
      -DSQLITE_ENABLE_MEMSYS5=1 -DSQLITE_ZERO_MALLOC=1
      -DSQLITE_OMIT_LOADEXTENSION=1 -DSQLITE_OMIT_AUTOINIT=1)
INC=(-I"$HERE/include" -I"$HERE/vendor")
PINVOKE="open=c,pread=c,pwrite=c,ftruncate=c,fsync=c,close=c,unlink=c,access=c,lseek=c,CreateFileA=kernel32,ReadFile=kernel32,WriteFile=kernel32,SetFilePointerEx=kernel32,SetEndOfFile=kernel32,FlushFileBuffers=kernel32,CloseHandle=kernel32,DeleteFileA=kernel32,GetFileSizeEx=kernel32,GetFileAttributesA=kernel32"
OBJS=(); cleanup(){ rm -f "${OBJS[@]}"; }; trap cleanup EXIT
for src in "$HERE/vendor/sqlite3.c" "$HERE/sqlite_shim.c" "$HERE/sqlite_vfs_disk.c" "$HERE/main_disk.c"; do
  o="$(mktemp --suffix=.obj)"; OBJS+=("$o")
  echo "[chibil]  $(basename "$src")"
  dotnet run -c Release --project "$CH" -- --target=coreclr "${DEFS[@]}" "${INC[@]}" -cc1 -cc1-input "$src" -cc1-output "$o"
done
echo "[chibil-link]  -> $OUT"
dotnet run -c Release --project "$LINK" -- -o "$OUT" --pinvoke="$PINVOKE" "${OBJS[@]}"
echo "built $OUT  (run in an empty dir: dotnet $OUT ; exit 55 == on-disk CRUD; creates sp3.db)"
```
Mark it executable in spirit (LF line endings; `.gitattributes` already forces LF for `.sh`).

- [ ] **Step 2: Full MSVC regression suite**

Run: `run-tests.cmd x64`
Expected: 0 failures (chibil codegen/`.obj` unchanged → IJW untouched; the new tests add to the CoreCLR count). Confirm the prior baseline (186 passed / 3 skipped before SP3a) grows only by the new tests with 0 failures.

- [ ] **Step 3: Full CoreCLR suite incl. all SP3a legs**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: 0 failures; `OsDispatchTests`, `PinvokeRoutingTests`, `PositionedIoTests`, `SqliteDiskTests` all green on both OSes.

- [ ] **Step 4: Update `samples/sqlite/README.md`**

Add an "On-disk persistence (SP3a)" subsection after the "Consuming from C#" section:
```markdown
## On-disk persistence (SP3a)

A cross-OS `sqlite3_vfs` (`sqlite_vfs_disk.c`) does real file I/O via native
P/Invoke — libc (`open`/`pread`/`pwrite`/`fsync`/…) on Linux, kernel32
(`CreateFileA`/`ReadFile`+`OVERLAPPED`/…) on Windows — picked at runtime by the
linker-synthesized `__chibil_os_is_windows()` intrinsic. The linker routes each
native symbol to its module via a `--pinvoke name=lib` map (lazy resolution means
the wrong-OS stubs are present but never loaded). `build-chibil-disk.sh` builds
`main_disk.c`, which writes `sp3.db`, closes, **reopens**, and `SELECT sum(a)`
returns 55 — the same `app.dll` on Windows and Linux/WSL
(`tests/Chibil.Tests/CoreClr/SqliteDiskTests.cs`). Single connection, no locking
yet; the SQLite byte-range lock protocol (fcntl / LockFileEx) is SP3b.
```

- [ ] **Step 5: Commit**

```bash
git add samples/sqlite/build-chibil-disk.sh samples/sqlite/README.md
git commit -m "docs(sqlite): on-disk persistence build script + README (SP3a)"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** §3.1 OS dispatch ↔ Task 1; §4.1 P/Invoke routing ↔ Task 2; §4.3 `OVERLAPPED` + positioned I/O ↔ Task 3; §3.2/§3.3 the VFS ↔ Task 4 Step 1; §5 harness ↔ Task 4 Step 2; §6 capstone + on-disk assertion ↔ Task 4 Steps 3-4; §6 focused infra tests ↔ Tasks 1-3; §6 non-regression ↔ Task 4 Step 5 + Task 5.
- **Build-order de-risking:** each new capability is proven by a fast focused test (Tasks 1-3) before the slow SQLite capstone (Task 4), so a failure localizes to one mechanism.
- **The Windows cwd trap:** the dotnet host's child cwd is NOT the app dir; `DiskRunner.RunWindows` sets `WorkingDirectory` so the relative `sp3.db`/`pio.tmp` lands in (and is probed from) the temp dir. WSL's `Run` already `cd`s into the staged dir.
- **Lazy P/Invoke is load-bearing:** both libc and kernel32 stubs exist in one image; only the running OS's are ever called, so the other set never attempts to load. The Task 2 test is the proof.
- **Untouched:** chibil codegen, the `.obj` format, `sqlite_shim.c`, `main.c`, and the SP2 export path — so `:memory:`, SP2, and the MSVC/IJW suite are the regression gates.
- **Type consistency:** `LinkToBytes(objs, libs, exportClass=null, pinvokeMap=null)` and `SymbolResolver.Resolve(..., pinvokeMap)` are used identically in Tasks 2-4; `PositionedIoTests.PinvokeMap()` is the single source of the map for Tasks 3-4; `DiskRunner.RunWindows(pe, out stdout, out probeSize, probeFile=null)` and `SqliteSmokeTests.RepoRootDir()` are defined in Task 3 and reused in Task 4.
