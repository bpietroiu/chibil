using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// MUSL-1 acceptance: link a C program whose libc symbols are all UNRESOLVED to a
/// single native library via the bulk <c>-l c</c> fallback (no per-symbol
/// <c>--pinvoke</c> map) — the exact mode the bash-to-IL bring-up uses, where the
/// ~220 libc externals all route to one library. The resolver synthesizes a
/// pinvokeimpl stub per (name, signature) and the program runs against real libc.
///
/// The mechanism (cross-object resolution, P/Invoke synthesis, variadic per-sig
/// stub forking, bulk <c>-l c</c>) already ships and is covered by
/// <see cref="VarargsTests"/> (native snprintf) and <see cref="PinvokeRoutingTests"/>
/// (getpid / kernel32). This test pins the milestone program that those don't:
/// a variadic writing to a real <c>FILE</c> stream (<c>printf</c> → stdout, vs
/// snprintf-to-buffer) PLUS a heap round-trip (<c>malloc</c>/<c>free</c>).
/// </summary>
public class MuslLinkTests
{
    // printf (variadic → stdout) + malloc/free (heap) + strlen/strcpy (fixed-arg),
    // every callee unresolved and bound through one `-l c`.
    const string Milestone = @"
typedef unsigned long size_t;
int   __cdecl printf(const char*, ...);
void* __cdecl malloc(size_t);
void  __cdecl free(void*);
size_t __cdecl strlen(const char*);
char* __cdecl strcpy(char*, const char*);
int main(void){
    char* p = (char*)malloc(8);
    strcpy(p, ""hi"");
    printf(""[%s]\n"", p);
    int n = (int)strlen(p);
    free(p);
    return n;            /* strlen(""hi"") == 2 */
}";

    [Fact]
    public void Milestone_printf_malloc_strlen_runs_on_linux_via_bulk_libc()
    {
        if (!WslRunner.Available()) return;

        byte[] obj = TestCompiler.CompileToObj(Milestone, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "musl1.obj");
        // Bulk fallback: one library, no per-symbol map. All of printf/malloc/free/
        // strlen/strcpy resolve to libc.so.6.
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });

        var (exit, output) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 2, $"expected exit 2 (strlen \"hi\"), got {exit}. Output:\n{output}");
        Assert.Contains("[hi]", output);   // printf actually wrote to the real stdout stream
    }

    [Fact]
    public void Native_data_import_stdout_initialized_from_libc()
    {
        if (!WslRunner.Available()) return;
        // `stdout` is a libc DATA global (FILE*). MSIL can't import native data, so
        // the linker synthesizes storage and initializes it at module load from
        // libc via NativeLibrary.GetExport. Writing through it must reach the real
        // stdout stream — proves the data-import mechanism end-to-end.
        const string src =
            "typedef struct _IO_FILE FILE;\n" +
            "extern FILE* stdout;\n" +
            "int fputs(const char*, FILE*);\n" +
            "int fflush(FILE*);\n" +
            "int main(void){ fputs(\"data-import-ok\\n\", stdout); fflush(stdout); return 7; }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "sout.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });
        var (exit, output) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 7, $"expected exit 7, got {exit}. Output:\n{output}");
        Assert.Contains("data-import-ok", output);   // the libc-initialized stdout pointer worked
    }

    [Fact]
    public void External_call_with_struct_pointer_sig_keeps_assembly_loadable()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // A struct type named ONLY in an external function's call-site signature
        // (never in a local/field/defined method) must still be copied, or the
        // synthesized P/Invoke stub's rewritten signature references TypeDef[0]
        // (VALUETYPE with a null token) — a corrupt signature that makes CoreCLR
        // reject the entire assembly at load ("entry point not found"). The call is
        // guarded by a global so it never executes (the stub targets a nonexistent
        // libc symbol); the program must load and return 42. Regression for the
        // bash-to-IL entry-point-at-scale bug (shell.o: make_word/sigsetjmp/…).
        const string src =
            "struct Opaque { int x; long y; };\n" +
            "extern struct Opaque* ext_make(int);\n" +
            "int g = 0;\n" +
            "int main(void){ if (g) ext_make(1); return 42; }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });
        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string o);
        Assert.True(exit == 42, $"expected 42 (assembly loaded with valid sigs), got {exit}. {o}");
    }

    static int RunViaHost(string src, out string output)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        return DotnetHostRunner.RunPeViaDotnetHost(pe, out output);
    }

    [Fact]
    public void Opaque_struct_pointer_in_defined_functions_jits_and_runs()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // An opaque struct (FILE = struct _IO_FILE, layout never defined) used only
        // by pointer has no TypeDef — it is referenced via a module-scoped TypeRef.
        // Without a synthesized empty TypeDef the TypeRef dangles and the JIT throws
        // InvalidProgramException when it compiles a method whose signature names it.
        // Must now JIT and run. Regression for the bash-to-IL InvalidProgram (FILE*).
        const string src =
            "typedef struct _IO_FILE FILE;\n" +
            "static FILE* getf(void){ return 0; }\n" +
            "static int usef(FILE* f){ return f == 0 ? 7 : 8; }\n" +
            "int main(void){ return usef(getf()); }\n";
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 7, $"expected 7, got {exit}. {o}");
    }

    [Fact]
    public void Runtimeconfig_forces_invariant_globalization()
    {
        // A chibil single-file image lacks the culture/resource infrastructure, so
        // the BCL formatting ANY exception message recurses to a fatal StackOverflow
        // (SR.GetResourceString -> CultureInfo -> resource grovel -> re-fault -> ...).
        // The emitted runtimeconfig must disable that path or every benign managed
        // exception crashes the process. Observed in bash: `echo a; echo b` (a
        // longjmp-driven NRE during reaping) fatally overflowed until invariant mode
        // was set. Pin both knobs so neither regresses.
        Assert.Contains("\"System.Globalization.Invariant\": true", RuntimeConfigText.Json);
        Assert.Contains("\"System.Resources.UseSystemResourceKeys\": true", RuntimeConfigText.Json);
    }

    [Fact]
    public void Setjmp_longjmp_resumes_across_frames()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // setjmp/longjmp lowered to managed exceptions: the setjmp function is wrapped
        // in `Lhead: .try { body } filter { ours? } handler { resume }`; longjmp throws
        // via a synthesized __chibil_longjmp helper that stashes (buf,val) and throws.
        // A cross-frame longjmp unwinds to the matching setjmp's filter, whose handler
        // resumes execution at the setjmp returning the value. No libc — the carrier +
        // throw are all managed. Regression for bash's test/[ builtin (test_exit).
        int exit = LinkRun(new[]
        {
            "typedef long jmp_buf[16];\n" +                       // concrete layout (&jb must decay)
            "extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);\n" +
            "jmp_buf jb;\n" +
            "static void deep(int n){ if (n == 0) longjmp(jb, 42); deep(n - 1); }\n" +
            "int main(void){ int v = setjmp(jb); if (v) return v; deep(5); return 1; }\n",
        }, out string o);
        Assert.True(exit == 42, $"expected 42 (resumed), got {exit}. {o}");
    }

    [Fact]
    public void Setjmp_does_not_rerun_code_before_the_setjmp_call_on_resume()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // chibil resumes a longjmp by re-entering the function. Code sequenced BEFORE
        // the setjmp call must run EXACTLY ONCE, else it re-executes on resume. Here
        // `count++` runs once on the first pass; the longjmp resumes after setjmp, so
        // count must still be 1. The old re-from-top model re-ran count++ -> 2.
        int exit = LinkRun(new[]
        {
            "typedef long jmp_buf[16];\n" +
            "extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);\n" +
            "jmp_buf jb;\n" +
            "int count;\n" +
            "int main(void){\n" +
            "  count++;\n" +                            // pre-setjmp: must run once
            "  int v = setjmp(jb);\n" +
            "  if (v == 0) longjmp(jb, 1);\n" +
            "  return count;\n" +                       // fixed: 1 ; bug: 2
            "}\n",
        }, out string o);
        Assert.True(exit == 1, $"expected 1 (count++ ran once), got {exit}. {o}");
    }

    [Fact]
    public void Setjmp_preserves_value_written_before_longjmp_on_resume()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // The exact MicroPython nlr shape: a slot is initialised before setjmp, then a
        // value is stored into it right before longjmp. On resume the pre-setjmp init
        // must NOT re-run and wipe that value (nlr.ret_val = NULL re-running clobbered
        // the exception object -> NullReferenceException in parse_compile_execute).
        int exit = LinkRun(new[]
        {
            "typedef long jmp_buf[16];\n" +
            "extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);\n" +
            "jmp_buf jb;\n" +
            "void *slot;\n" +
            "int main(void){\n" +
            "  slot = 0;\n" +                           // pre-setjmp init: must run once
            "  int v = setjmp(jb);\n" +
            "  if (v == 0) { slot = (void*)0x55; longjmp(jb, 1); }\n" +
            "  return slot == (void*)0x55 ? 1 : 0;\n" + // fixed: 1 ; bug: 0
            "}\n",
        }, out string o);
        Assert.True(exit == 1, $"expected 1 (slot preserved across resume), got {exit}. {o}");
    }

    [Fact]
    public void Wide_bitfield_at_high_offset_round_trips()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // A bitfield in a 64-bit storage unit at a high bit offset (`unsigned long
        // hi : 8` after `lo : 56`) must store/load correctly. chibil's bitfield STORE
        // did mask/shift/merge in 32-bit, so `value << 56` masked the count to `<< 24`,
        // the scratch was 32-bit, and the clear-mask was truncated -> the high field
        // always read back 0. (This froze MicroPython's parser: its rule_stack packs
        // `rule_id : 8` at bit offset 56 of a size_t, so every popped rule read 0 and
        // the parser looped pushing rule 0 until the heap was exhausted.)
        // Mirror MicroPython's rule_stack_t exactly: pointer-based assign into an
        // array element, a uint8_t RHS, and a trailing regular field.
        const string src =
            "struct s { unsigned long long lo : 56; unsigned long long hi : 8; unsigned long long tail; };\n" +
            "int main(void){\n" +
            "  struct s a[2];\n" +
            "  struct s *p = &a[0];\n" +
            "  unsigned char v = 56;\n" +
            "  p->lo = 123;\n" +
            "  p->hi = v;\n" +
            "  p->tail = 0;\n" +
            "  if (p->lo != 123) return 1;\n" +   // low field intact
            "  if (p->hi != 56) return 2;\n" +    // high field round-trips (was 0)
            "  return 42;\n" +
            "}\n";
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 42, $"expected 42 (wide bitfields round-trip), got {exit}. {o}");
    }

    [Fact]
    public void Array_global_decays_to_pointer_when_passed()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // Passing an array GLOBAL to a pointer parameter decays it to the address of
        // its first element. The JIT accepts the managed pointer to the array
        // value-type (&$ArrayType$) where a native int is required, so this must run.
        const string src =
            "int g_arr4[4] = {10, 20, 30, 40};\n" +
            "static int sum4(int* p){ return p[0]+p[1]+p[2]+p[3]; }\n" +
            "int main(void){ return sum4(g_arr4); }\n";   // 100
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 100, $"expected 100, got {exit}. {o}");
    }

    [Fact]
    public void Main_argc_argv_entry_marshals_argv()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // `int main(int argc, char** argv)` must now link: the entry synthesizes a
        // char** from the host command line (argv[0] = program path, argv[argc] =
        // NULL). No libc — pure pointer checks — so it runs on the bare host.
        const string src =
            "int main(int argc, char** argv){\n" +
            "  if (argc < 1) return 10;\n" +
            "  if (argv[0] == 0) return 11;\n" +
            "  if (argv[argc] != 0) return 12;   /* NUL terminator at index argc */\n" +
            "  if (argv[0][0] == 0) return 13;   /* program path is non-empty */\n" +
            "  return 42;\n" +
            "}\n";
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 42, $"expected 42, got {exit}. {o}");
    }

    static int LinkRun(string[] sources, out string output)
    {
        var objs = new List<ObjectFile>();
        for (int i = 0; i < sources.Length; i++)
            objs.Add(ObjectFile.Load(
                TestCompiler.CompileToObj(sources[i], Chibil.TargetProfile.CoreClr), $"tu{i}.obj"));
        byte[] pe = LinkPipeline.LinkToBytes(objs, new List<string>());
        return DotnetHostRunner.RunPeViaDotnetHost(pe, out output);
    }

    [Fact]
    public void Cross_object_data_global_resolves_by_name()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // An initialized global defined in TU 0 and read from TU 1 — the data
        // analog of cross-object function resolution. Real multi-TU bash shares
        // dozens of such globals (loop_level, extglob_flag, …).
        int exit = LinkRun(new[]
        {
            "int g = 42;",
            "extern int g; int main(void){ return g; }",
        }, out string o);
        Assert.True(exit == 42, $"expected 42 (cross-TU global g), got {exit}. {o}");
    }

    [Fact]
    public void Uninitialized_external_global_gets_bss_storage()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // `int counter;` at file scope is a COMMON symbol (external tentative def,
        // Sect=0/Value=size), not a section-bound slot — previously dropped, so the
        // program failed to link. The linker must now allocate a zero-init .bss slot.
        int exit = LinkRun(new[]
        {
            "int counter; int main(void){ counter++; counter++; return counter; }",
        }, out string o);
        Assert.True(exit == 2, $"expected 2 (bss-allocated common global), got {exit}. {o}");
    }

    [Fact]
    public void Cross_object_bss_global_mutated_then_read()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // An UNINITIALIZED (.bss) global defined in TU 0, mutated by a function in
        // TU 0, and read from TU 1 — exercises cross-TU resolution of a BSS global
        // alongside a cross-TU function call.
        int exit = LinkRun(new[]
        {
            "int counter; void bump(void){ counter++; }",
            "extern int counter; extern void bump(void);" +
            " int main(void){ bump(); bump(); bump(); return counter; }",
        }, out string o);
        Assert.True(exit == 3, $"expected 3 (cross-TU bss global), got {exit}. {o}");
    }

    [Fact]
    public void Cross_tu_function_pointer_in_static_data_resolves()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // A static initializer — a function-pointer global AND a struct-array dispatch
        // table — referencing a function DEFINED IN ANOTHER TU. chibil emits the COFF
        // symbol for the cross-TU function with a stale (section,value) that collides
        // with an unrelated local method (e.g. the first method at .text+0), so the
        // linker must resolve it by NAME to ldftn the right method. Regression for
        // bash's builtins table dispatching to a wild pointer (native crash).
        int exit = LinkRun(new[]
        {
            "int the_func(int x) { return x + 100; }\n",
            "extern int the_func(int);\n" +
            "int (*g_fp)(int) = the_func;\n" +
            "struct { const char *name; int (*fn)(int); } tbl[] = { { \"f\", the_func } };\n" +
            "int main(void){ return g_fp(5) + tbl[0].fn(7); }\n",  // 105 + 107 = 212
        }, out string o);
        Assert.True(exit == 212, $"expected 212, got {exit}. {o}");
    }

    [Fact]
    public void Cross_tu_call_to_chibil_defined_variadic_works()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // A chibil-defined variadic (Layer-1 va-buffer ABI: hidden trailing __va param)
        // called from ANOTHER TU arrives as a Layer-2 cdecl MemberRef (no __va). The
        // linker must bridge it with an adapter that packs the varargs and calls the
        // definition. Exercises both a 3-vararg and a 0-vararg call. Regression for the
        // bash InvalidProgram (builtin_error/builtin_usage cross-TU).
        int exit = LinkRun(new[]
        {
            "typedef __builtin_va_list va_list;\n" +
            "int g_sum;\n" +
            "void vsum(int n, ...){ va_list ap; __builtin_va_start(ap, n); int s = 0;" +
            " for (int i = 0; i < n; i++) s += __builtin_va_arg(ap, int); __builtin_va_end(ap); g_sum = s; }\n",
            "extern int g_sum; extern void vsum(int, ...);\n" +
            "int main(void){ vsum(0); int z = g_sum; vsum(3, 10, 20, 12); return g_sum + z; }\n",  // 42 + 0
        }, out string o);
        Assert.True(exit == 42, $"expected 42, got {exit}. {o}");
    }

    [Fact]
    public void Main_three_arg_entry_marshals_envp()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // bash's real entry is `int main(int, char**, char**)`. envp must be the
        // process environment as a NUL-terminated char** of "KEY=VALUE" UTF-8 C
        // strings (the third main parameter), not NULL.
        const string src =
            "int main(int argc, char** argv, char** envp){\n" +
            "  if (argc < 1) return 10;\n" +
            "  if (argv[argc] != 0) return 11;\n" +     // argv NUL-terminated
            "  if (envp == 0) return 12;\n" +           // envp present
            "  int n = 0; while (envp[n]) n++;\n" +     // envp NUL-terminated
            "  if (n == 0) return 13;\n" +              // and non-empty
            "  for (int i = 0; i < n; i++) {\n" +       // every entry is KEY=VALUE
            "    char* e = envp[i]; int has = 0;\n" +
            "    for (int j = 0; e[j]; j++) if (e[j] == '=') { has = 1; break; }\n" +
            "    if (!has) return 14;\n" +
            "  }\n" +
            "  return 56;\n" +
            "}\n";
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 56, $"expected 56 (envp marshalled), got {exit}. {o}");
    }
}
