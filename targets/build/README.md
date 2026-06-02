# chibil bash-5.3 build harness

The chibil-side additions for compiling **GNU bash 5.3** to MSIL. The upstream
bash source is **not** vendored here — it's downloaded and patched. See the
full walkthrough in [`../../CompileBash.md`](../../CompileBash.md).

| File | What it is |
|---|---|
| `chibil-bash-5.3.patch` | the only source changes — 6 files (`jobs.c/.h`, `execute_cmd.c`, `subst.c`, `variables.c/.h`) implementing fork-on-CoreCLR (`#ifdef CHIBIL_REEXEC`). Apply against a pristine `bash-5.3` tree. |
| `Makefile.chibil` | compiles the 223 TUs to IL (`--target=coreclr -nostdinc -mlp64 -DCHIBIL_REEXEC=1`) and links `bash.dll`. Copy into the bash source root. |
| `chibil-sources.list` | the 223 translation units to compile. Copy into the bash source root. |
| `config-tweaks.sh` | disables `USING_BASH_MALLOC` / `HAVE_ARC4RANDOM` in the generated `config.h` (run after `./configure`). |

## Quick apply

```bash
# pristine bash 5.3 (matches the tarball)
git clone https://git.savannah.gnu.org/git/bash.git bash-5.3
cd bash-5.3 && git checkout bash-5.3

./configure && make            # generate config.h + parser/builtins; reference native build
sh  ../build/config-tweaks.sh  # disable bash-malloc / arc4random
git apply ../build/chibil-bash-5.3.patch
cp ../build/Makefile.chibil ../build/chibil-sources.list .

make -f Makefile.chibil -j$(nproc)
dotnet bash.dll --norc --noprofile -c 'echo hi; seq 1 3 | wc -l'
```

The patch is verified to `git apply --check` cleanly against the `bash-5.3` tag.
Design rationale: [`../../docs/superpowers/specs/2026-06-02-bash-fork-on-coreclr-design.md`](../../docs/superpowers/specs/2026-06-02-bash-fork-on-coreclr-design.md).
