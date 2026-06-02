# Compiling GNU bash to MSIL with chibil

This guide walks through compiling **GNU bash 5.3** from source into a single
**.NET/MSIL assembly** (`bash.dll`) using the `chibil` C-to-MSIL compiler and the
`chibil-link` linker, then running it on CoreCLR. The result is real bash — its
own C source compiled to IL — running on the .NET runtime and calling the host's
`libc`.

> **Status:** the resulting `bash.dll` passes a 57/57 shell-feature test matrix
> (variables, arithmetic, conditionals, loops, functions, arrays, command
> substitution with full state transfer, external commands, redirections,
> heredocs, and pipelines). Interactive mode and job control are partial. See
> [Known limitations](#known-limitations) and the design doc
> [`docs/superpowers/specs/2026-06-02-bash-fork-on-coreclr-design.md`](docs/superpowers/specs/2026-06-02-bash-fork-on-coreclr-design.md).

The bring-up is **Linux-only** (the IL image binds Linux `libc.so.6` /
`libtinfo.so.6`), so everything below runs under **WSL** (or any Linux box with
.NET installed).

---

## 1. Prerequisites

| Tool | Why | Notes |
|---|---|---|
| **WSL** (Ubuntu) or Linux | the image links the Linux glibc/ncurses | the build's `dotnet` is the Linux dotnet |
| **.NET 10 SDK** | builds + runs chibil, chibil-link, and `bash.dll` | `dotnet --version` ≥ 10 |
| **gcc, make, bison, autoconf** | bash's native `./configure` + generated sources (parser, `builtins/*.c`) | standard `build-essential` |
| **The chibil repo** | the compiler + linker | this repository |

```bash
sudo apt-get install -y build-essential bison autoconf wget
```

All paths below assume the chibil repo at `/mnt/d/sandbox/chibil` and that bash +
musl sources live under `targets/`. Adjust `CHIBILDIR` in `Makefile.chibil` if
your checkout is elsewhere.

---

## 2. Build chibil and chibil-link

```bash
cd /mnt/d/sandbox/chibil
dotnet build chibil/Chibil.csproj            -c Debug
dotnet build tools/chibil-link/ChibilLink.csproj -c Debug
```

This produces:
- `chibil/bin/Debug/net10.0/chibil.dll` — the C→MSIL compiler (`--target=coreclr`).
- `tools/chibil-link/bin/Debug/net10.0/chibil-link.dll` — the linker that merges
  the per-TU COFF objects into one PE and synthesizes the P/Invokes to `libc`.

---

## 3. Get musl 1.2.6 (used only for its headers)

chibil rejects glibc's gcc-builtin-laden `stddef.h`/`stdarg.h`, so we compile bash
against **musl's** clean headers with `-nostdinc`. We only need musl's *headers*,
but a `configure && make` is the simplest way to materialize the generated ones
(`obj/include/bits/alltypes.h`, etc.).

```bash
cd /mnt/d/sandbox/chibil/targets
wget https://musl.libc.org/releases/musl-1.2.6.tar.gz
tar xf musl-1.2.6.tar.gz
cd musl-1.2.6
./configure
make            # generates obj/include/* (alltypes.h, syscall.h, version.h, …)
```

The chibil build adds these to the include path:
`-I arch/x86_64 -I arch/generic -I obj/include -I include`.

> We compile against musl headers but **link the host glibc at runtime**: musl's
> `libc.so` can't be `dlopen`'d into the glibc/.NET host (its `__init_tls` would
> hijack CoreCLR's thread pointer). glibc is already loaded and initialized by the
> runtime, so there's no init/TLS conflict.

---

## 4. Get bash 5.3, apply the chibil patch, and configure

The upstream bash source is **not vendored** in this repo. You download a pristine
`bash-5.3` and apply the chibil bring-up changes from
[`targets/build/`](targets/build/) (which holds the patch + build harness — see
[`targets/build/README.md`](targets/build/README.md)).

```bash
cd /mnt/d/sandbox/chibil/targets

# pristine bash 5.3 — either the GNU git tag …
git clone https://git.savannah.gnu.org/git/bash.git bash-5.3
cd bash-5.3 && git checkout bash-5.3
# … or the release tarball:  wget https://ftp.gnu.org/gnu/bash/bash-5.3.tar.gz && tar xf …

./configure                 # generates config.h, version.h, pathnames.h, …
make                        # native build: generates y.tab.c, syntax.c, builtins/*.c
                            # (also gives you a reference native bash to compare against)

# apply the chibil changes
sh  ../build/config-tweaks.sh                 # config.h: disable bash-malloc / arc4random
git apply ../build/chibil-bash-5.3.patch      # the 6-file fork-on-CoreCLR source changes
cp  ../build/Makefile.chibil ../build/chibil-sources.list .
```

The native `make` is the easiest way to produce every *generated* C source the IL
build also needs (the yacc parser `y.tab.c`, `syntax.c`, `builtins/*.c` and
`builtext.h` from the `.def` files, `version.h`, `pathnames.h`).

`config-tweaks.sh` turns off two `config.h` defaults: **`USING_BASH_MALLOC`** (the
bundled `sbrk` allocator fights the .NET heap — route `malloc`/`free` to host libc)
and **`HAVE_ARC4RANDOM`** (absent here; bash falls back to `getrandom`).

---

## 5. The chibil build harness

After the steps above, the bash source root contains the harness from
[`targets/build/`](targets/build/):

- **`Makefile.chibil`** — compiles every TU with one uniform flag set and links the
  result. Key flags: `--target=coreclr -nostdinc -mlp64 -DHAVE_CONFIG_H -DSHELL
  -DCHIBIL_REEXEC=1`.
  - `-mlp64` selects the **LP64** data model (`long`/`size_t` = 8 bytes) — bash on
    Linux is LP64; chibil defaults to LLP64 (Windows).
  - `-nostdinc` keeps host glibc headers out; only the `-I` musl paths are used.
  - `-DCHIBIL_REEXEC=1` enables the fork-on-CoreCLR support (see below).
- **`chibil-sources.list`** — the 223 translation units (the same set the native
  bash binary links, plus `lib/sh/random.c` and `lib/sh/oslib.c`).
- The runtimeconfig is emitted automatically (invariant globalization — required, or
  the single-file image stack-overflows the first time the BCL formats an exception).

### 5a. fork-on-CoreCLR source changes (the patch)

CoreCLR has no usable `fork()` (forking the CLR corrupts even the parent). The
bring-up replaces bash's process model with `posix_spawn` + re-exec. This is the
entire content of `chibil-bash-5.3.patch` — **6 files**, all guarded by
`#if defined (CHIBIL_REEXEC)`:

| File | Change |
|---|---|
| `jobs.c`, `jobs.h` | `make_child_posix_spawn` (externals), `chibil_spawn_comsub` (`$(...)` via re-exec) |
| `execute_cmd.c` | route externals/pipelines/redirections to `posix_spawn`; run safe builtin pipeline stages in-parent |
| `subst.c` | command substitution via re-exec instead of `make_child` |
| `variables.c`, `variables.h` | `chibil_state_dump` — serialize functions/vars/positional params into the comsub child |

The full design and rationale are in
[`docs/superpowers/specs/2026-06-02-bash-fork-on-coreclr-design.md`](docs/superpowers/specs/2026-06-02-bash-fork-on-coreclr-design.md).

---

## 6. Build `bash.dll`

```bash
cd /mnt/d/sandbox/chibil/targets/bash-5.3
make -f Makefile.chibil -j$(nproc)
```

This compiles each of the 223 TUs to a COFF object under `_il/` and links them into
`bash.dll` (~2 MB) plus `bash.runtimeconfig.json`. The link line is:

```
chibil-link -o bash.dll -lc -ltinfo  _il/*.obj
```

`chibil-link` probes each `-l` library (like a real linker) and binds every symbol
to the one that actually exports it — `printf`/`malloc` → `libc.so.6`,
`tgetent`/`tputs` → `libtinfo.so.6`.

Useful targets:
```bash
make -f Makefile.chibil gen          # (re)generate builtins/*.c from *.def
make -f Makefile.chibil run ARGS='--norc --noprofile -c "echo hi"'
make -f Makefile.chibil clean
```

---

## 7. Run it

```bash
cd /mnt/d/sandbox/chibil/targets/bash-5.3
dotnet bash.dll --norc --noprofile -c 'echo hello; seq 1 5 | wc -l'
```
```
hello
5
```

More examples (all pass):
```bash
dotnet bash.dll --norc --noprofile -c 'x=world; echo "hi $x"'
dotnet bash.dll --norc --noprofile -c 'echo hello | tr a-z A-Z'          # HELLO
dotnet bash.dll --norc --noprofile -c 'ls /etc > /tmp/x; wc -l < /tmp/x'
dotnet bash.dll --norc --noprofile -c 'cat <<EOF
heredoc works
EOF'
dotnet bash.dll --norc --noprofile -c \
  'fact(){ [ $1 -le 1 ] && echo 1 || echo $(($1*$(fact $(($1-1))))); }; fact 5'   # 120
```

- **`-c '<script>'`** is the well-tested entry point.
- **`--norc --noprofile`** is recommended (skips startup files — faster, and avoids
  a known quirk where this bash sources them even non-interactively).

---

## Known limitations

- **Performance:** each `$(...)`, external command, and pipeline spawns a fresh
  `dotnet bash.dll` (CLR cold start ~1–2 s). Comsub-heavy scripts are slow and can
  occasionally time out under load. Correctness is solid; speed is the trade-off of
  the re-exec design (an in-process fast path / AOT would address it).
- **Interactive mode** (`bash` with no `-c`) reaches the readline/job-control init
  but isn't complete — terminal control, `^C`/SIGINT, and `fg`/`bg` need the signals
  + job-control work. Use `-c` for now.
- **Not yet handled:** subshells `( … )`, background `&`, full job control,
  process substitution `<( )`, and arrays inside command substitution.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `EntryPointNotFoundException: 'tgetent' in 'libc.so.6'` | termcap not linked — ensure the link line has `-ltinfo` and `libtinfo.so.6` is installed |
| Stack overflow on the first error message | runtimeconfig missing invariant globalization — let `chibil-link`/the Makefile emit `bash.runtimeconfig.json` |
| `cannot allocate N bytes` at startup | `USING_BASH_MALLOC` still on, or `lib/malloc` not filtered out |
| ~4 GB allocation / `xreallocarray` abort | missing `-mlp64` (LP64 data model) |
| `command not found` for generated builtins | run `make -f Makefile.chibil gen` (needs host `gcc`) |
| Runs on Windows fail | the image is Linux-only — run under WSL with the Linux `dotnet` |
