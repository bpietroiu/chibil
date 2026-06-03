# Chibil — fork additions

This fork adds an in-house linker and a set of compiler improvements, and exercises
them against SQLite, bash, MicroPython, and QuickJS — the last of which runs at
**zero-error test262 conformance** across ~29,400 tests (see below).

### chibil-link (in-house linker)

Emits a pure-MSIL (`ILOnly`) .NET assembly directly from chibil's COFF objects — no
`link.exe`, no Windows required.

- Merges COFF objects and predicts the output metadata rows.
- Lays initialized globals into FieldRVA data; synthesizes a `<Module>` static
  constructor that applies pointer relocations and initializes native data imports.
- GNU-style argument parsing: `-shared`, `-e`/`--entry`, `-L`/`-l`, `@response`
  files, attached or separated forms; assembly identity from `-o`.
- `argv`/`envp` marshalling for `int main(int, char**, char**)`.
- Synthesizes P/Invoke stubs to bind libc at run time.
- Emits a native **apphost launcher** (`foo.exe` on Windows, `foo` on Linux) next to
  the managed `foo.dll` for executable targets — copying and patching the .NET apphost
  template the way `dotnet build` does, so the program runs as `./foo` instead of
  `dotnet foo.dll`. Best-effort: if no SDK/runtime template is found the link still
  succeeds and the dll stays runnable via `dotnet foo.dll`.
- `-g` emits `DebuggableAttribute` so a .NET debugger binds breakpoints and locals.

### Compiler improvements

- **Portable PDB debug info** — emitted as an embedded Portable PDB (sequence points
  with column spans, nested local scopes), so a real .NET debugger steps the C
  source, binds breakpoints (including inside `for(;;)` headers), and shows locals.
- Prefix `__attribute__((...))` parsing; forward-declared (incomplete) enums.
- Correct bitfield codegen — 64-bit high-bit stores and small-storage (`u8`/`u16`)
  reads both fixed.
- Union compound-literal initializers copy the whole union (designated non-first
  members no longer truncated).
- Flexible-array-member globals, and Mutable initialized globals, sized by their data
  extent; static-data function pointers to external (libc) functions bound via
  load-time P/Invoke stubs.
- Unused `extern` declarations emit no symbol (lazy field registration).
- `setjmp`/`longjmp` lowered to managed-exception resumption that resumes at the
  `setjmp` call, including when it is inside a loop.

### Ports tested

- **SQLite** — compiles and runs from the amalgamation (opaque handles, by-value
  structs, function-pointer tables).
- **bash 5.3** — compiles to MSIL, runs on CoreCLR; 57/57 feature tests pass
  (externals, redirections, heredocs, pipelines, command substitution with full
  state transfer; `fork` via `posix_spawn` re-exec). Open: subshells, background jobs.
- **MicroPython** (minimal port) — all 138 translation units compile and link into a
  single 3.4 MB assembly; boots to the REPL and evaluates Python (arithmetic, lists,
  comprehensions), catching exceptions and printing tracebacks. Open: an
  `InvalidProgramException` in `mp_iternext` (iterator dispatch).
- **QuickJS** (`quickjs-2025-09-13`) — Bellard's JS engine; all 7 interpreter
  translation units compile and link into a single `qjs.dll`, and it **runs JavaScript
  at zero-error test262 conformance**: exceptions, classes, generators, destructuring,
  `RegExp`, `Map`/`Set`, `BigInt`, `Proxy`, and the async/`Promise` job queue.

### QuickJS test262 conformance

`run-test262.c` compiles and links with chibil into `run-test262.dll`, run against a
fresh `tc39/test262` clone:

| Area | errors / tests |
|---|---|
| `language/types` | 0 / 113 |
| `language/statements` | 0 / 9,113 |
| `language/expressions` | 0 / 10,665 |
| `built-ins/Array` | 0 / 2,900 |
| `built-ins/String` | 0 / 1,198 |
| `built-ins/Object` | 0 / 3,402 |
| `built-ins/RegExp` (prototype, named-groups) | 0 / 511 |
| `built-ins/Map` | 0 / 171 |
| `built-ins/Promise` (async / job queue) | 0 / 640 |
| **total** | **0 / ~29,400** |

Zero chibil-caused failures across ~29,400 conformance tests. (The only failures
anywhere are ~61 `RegExp/property-escapes` tests — a Unicode-data-version skew between
QuickJS's bundled `libunicode` and the freshly-cloned suite, which fail on native
QuickJS too.) See [CompileQuickJS.md](CompileQuickJS.md) for the full write-up.

---

# Chibil C compiler

## What is chibil

Chibil is a C compiler based on [chibicc](https://github.com/rui314/chibicc) rewritten in C# and updated to target .NET IL (MSIL).

It is complete enough to run [DOOM](samples/doom) (PureDOOM).

## Pipeline

Chibil takes C source files and generates COFF OBJ files. These OBJ files are binary-compatible with OBJ files produced by the MSVC compiler in `/clr` mode. link.exe from Visual Studio is used to link the object files together and produce final executables. One can actually mix and match C++/CLI and chibil-produced object files.

Chibil will probably have its own linker later, if for no other reason, just so we don't need Windows.

## Debugging

Line numbers and locals work as expected. You can step through the C code in a .NET debugger.

## Standard C library

There isn't one.

There is a minimal stub of a C runtime library in the [crt](crt) directory. This provides a runnable `main` that takes `string[]` of arguments and dispatches to the C-standard `main` with `argc` and `argv`. The produced assembly is converted to a COFF object file using the [asm2obj](tools/asm2obj) utility and can be linked together with the C code to form something with a runnable `main`.

## Consuming C code from .NET code

This is not complete yet. The code is generated into global namespace so if you want to consume the compiled code from elsewhere (i.e. don't intend to just run the EXE), you'll need to use reflection such as [Module.GetMethod](https://learn.microsoft.com/dotnet/api/system.reflection.module.getmethod) to find the methods and reflection-invoke them.

## Useful tools

The tools/coffobjdumper.cs file contains a COFF OBJ dumper that dumps .NET OBJ files. It's a good complement for ILDASM (the desktop CLR ILDASM!) that can dump the .NET metadata from COFF OBJ files, but doesn't show method bodies, and dumpbin.exe that can dump various things from COFF objects, but not much in terms of .NET metadata.
