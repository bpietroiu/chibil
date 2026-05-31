# Self-contained Linux builds

Chibil can compile C to a runnable **pure-MSIL .NET assembly on Linux with no
Windows tooling** — no `link.exe`, no MSVC. The C is compiled to a pure-MSIL
managed COFF object by chibil's CoreCLR target, then linked into a single PE +
`runtimeconfig.json` by the in-house linker [`chibil-link`](../../tools/chibil-link),
and run on CoreCLR with `dotnet app.dll`.

## Pipeline

```
a.c ─┐ chibil --target=coreclr -cc1   →  a.obj  (pure-MSIL managed COFF)
b.c ─┘                                   b.obj
a.obj b.obj ─► chibil-link -lc -o app.dll   →  app.dll + app.runtimeconfig.json
                                                 └─ dotnet app.dll
```

- `--target=coreclr` makes chibil emit a pure-MSIL object (no `/clr` mixed-mode
  IJW thunks), so the result loads on CoreCLR. It is the default on non-Windows.
- `chibil-link` merges the objects' metadata, resolves cross-object calls, and
  turns any symbol left unresolved into a native **P/Invoke** bound to a library
  named by `-l` (e.g. `-lc` → `libc.so.6`).

## build.sh

[`build.sh`](build.sh) orchestrates both tools:

```sh
# requires the .NET 10 SDK on PATH (dotnet --version)
bash samples/linux/build.sh app.dll greet.c main.c -lc
dotnet app.dll
```

## Worked example (cross-object call + real libc)

```c
// greet.c
int puts(const char*);
void greet(void){ puts("hello from chibil on linux"); }
```
```c
// main.c
void greet(void);
int main(void){ greet(); return 0; }
```
```
$ bash samples/linux/build.sh app.dll greet.c main.c -lc
$ dotnet app.dll
hello from chibil on linux
```

`main` calls `greet` (resolved across objects), and `greet` calls `puts`
(synthesized as a P/Invoke into `libc.so.6`).

## Status & limitations

Verified end-to-end on Ubuntu 24.04 with the .NET 10 SDK. Current limitations of
the CoreCLR/Linux path:

- `int main(void)` is supported; `int main(int, char**)` argv marshalling is not
  yet wired (the linker reports a clear error).
- Variadic P/Invoke (e.g. calling `printf` with extra arguments) is not yet
  supported — use non-variadic libc entry points (`puts`, `fputs`, `write`, …).
- The P/Invoke MVP binds every unresolved symbol to the **first** `-l` library;
  mixing libraries (`-lc -lm`) warns and may need per-symbol resolution.
- No automatic libc/headers are bundled — declare the libc prototypes you use.
