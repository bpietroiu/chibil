# Debugging chibil assemblies & consuming them from C#

chibil compiles C to a real .NET assembly. With debug info enabled you can
**set breakpoints in your `.c` source and step through it in Visual Studio**, and
with an export class you can **call your C functions from a normal C# project**.

This guide is the end-to-end recipe, validated against real Visual Studio.

---

## What you get

A chibil-linked PE is an ordinary managed assembly that carries:

- An **embedded Portable PDB** mapping IL offsets back to your `.c` source lines,
  with **named locals** — so VS shows source, breakpoints, and the Locals/Autos
  window. No sidecar `.pdb` file to ship.
- Optionally a **public façade class** (`--export-class`) so C# can name and call
  your functions (C# cannot reference the CLR's anonymous `<Module>` type).
- With **`-g`**, the assembly is marked `[Debuggable(isJITOptimizerDisabled: true)]`
  so the JIT leaves the code unoptimized and **breakpoints actually bind**.

---

## The two flags that matter

| Flag | Tool | Effect |
| --- | --- | --- |
| `-g` | chibil-link | Mark the assembly debuggable (JIT optimizer off). **Required for breakpoints to bind.** |
| `--export-class=<Ns.Type>` | chibil-link | Emit a `public static` façade class that forwards to each exported C function. **Required to call C from C#.** |

> The embedded PDB itself is **always** emitted. `-g` only controls the
> `Debuggable` attribute. This mirrors `gcc`:
> - **no `-g`** → optimized code + PDB → good stack traces, but you can't freely
>   set breakpoints ("no executable code of the debugger's target code type is
>   associated with this line").
> - **`-g`** → unoptimized code + PDB → full breakpoint / step / locals debugging.

---

## Recipe A — debug a whole C program in VS

```powershell
# 1. compile each .c to a managed object
dotnet chibil.dll  -c --target=coreclr  prog.c  -o prog.obj

# 2. link with -g (debuggable)
dotnet chibil-link.dll  -g  -o prog.dll  prog.obj
```

`chibil-link` also writes `prog.runtimeconfig.json` next to the dll so the .NET
host can run it: `dotnet prog.dll`.

To debug in Visual Studio:

1. **File ▸ Open ▸ Project/Solution**, or open the folder, and add `prog.dll`.
   Easiest path: make a throwaway C# console project that references it (Recipe B)
   and debug *that*, or open `prog.dll` directly and use **Debug ▸ Step Into New
   Instance**.
2. Open the original `.c` file, click the gutter on a code line to set a
   breakpoint.
3. **F5**. Execution stops at the line; **Locals** shows your C variables by name.

---

## Recipe B — consume a C library from a C# project

### 1. Build the library with an export class (and `-g` to debug into it)

```powershell
dotnet chibil.dll  -c --target=coreclr  mathlib.c  -o mathlib.obj
dotnet chibil-link.dll  -g  -o mathlib.dll  --export-class=Acme.Native  mathlib.obj
```

A C function `int sq(int x)` becomes `public static int Acme.Native.sq(int x)`.
(External-linkage functions are exported; `static` C functions and `main` are not.)

### 2. Reference it from C#

```xml
<!-- host.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="a">               <!-- see "assembly name" gotcha below -->
      <HintPath>..\mathlib.dll</HintPath>
      <Private>true</Private>             <!-- copy next to host.exe at build -->
    </Reference>
  </ItemGroup>
</Project>
```

```csharp
class Program
{
    static void Main()
        => System.Console.WriteLine("sq(7) = " + Acme.Native.sq(7));   // -> 49
}
```

`dotnet run` prints `sq(7) = 49`. Set a breakpoint inside `sq` in `mathlib.c`,
press **F5** in VS, and you step from C# straight into the C source.

---

## Gotchas (read before you file a bug)

- **Breakpoints won't bind without `-g`.** The single most common symptom is VS
  saying *"no executable code of the debugger's target code type is associated
  with this line."* That means the assembly was linked without `-g`, so the JIT
  optimized the method away. Re-link with `-g`.

- **Assembly name is currently `a`.** chibil-link names every output assembly `a`
  (like `a.out`), regardless of `-o`. So the C# `<Reference Include="...">` must
  say `a`, and the file on disk that the runtime loads must be named to match the
  identity (e.g. copy/rename to `a.dll`), or you'll get
  `FileNotFoundException: Could not load file or assembly 'a'`. Tracked for a fix
  (derive identity from `-o`, like `gcc -o`).

- **chibil-link needs a `main`.** It always synthesizes an executable entry point,
  so a pure library must include a `main` (it's ignored when used as a library).
  A `-shared`/library mode is tracked.

- **Native dependencies are Linux.** chibil's libc calls are P/Invokes to
  `libc.so.6` etc. A function that calls into libc therefore only loads on Linux.
  A **pure** C function (no library calls) is plain IL and loads/runs/debugs on
  **Windows** too — that's what makes the VS story work for self-contained code.

- **Use Visual Studio (or VS Code's C# debugger), not netcoredbg.** netcoredbg
  does not currently bind breakpoints against chibil's embedded PDB / `<Module>`
  global methods. VS is the validated, supported target.

---

## Why `-g` is the magic switch (one paragraph)

The embedded PDB is necessary but not sufficient. A debugger binds a source
breakpoint by mapping *file:line → method + IL offset* (the PDB) and then
*IL offset → native address* (the JIT's debug map). The second mapping only
exists if the JIT compiles the method **unoptimized**. The CLR decides that from
the assembly-level `DebuggableAttribute`: every C# Debug build emits it; chibil
emits it when you pass `-g`. Without it the method is optimized — inlined, locals
enregistered — and there is no clean native code to stop at, hence the
"no executable code" error even though the PDB is perfect.
