using System.IO;
using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

/// <summary>M4.5 acceptance: real GNU bash 5.3 — chibil-compiled to MSIL and linked
/// against the managed musl object set + SandboxPal (zero native libc) — runs as a
/// green-process and executes a builtin with stdout captured.
/// Build the image first:  dotnet build targets/sandbox/SandboxBash.proj -c Release</summary>
[Collection("SandboxKernel")]
public class BashTests
{
    static string BashDll() =>
        Path.Combine(SandboxToolBuilder.RepoRoot(), "build", "bin", "sandbox", "bash.dll");

    static readonly string[] Coreutils = { "cat", "wc", "true", "false", "head", "tail", "ls", "sort", "grep" };

    static string ToolDll(string name) =>
        Path.Combine(SandboxToolBuilder.RepoRoot(), "build", "bin", "sandbox", name + ".dll");

    static bool ExternalsBuilt() => System.Array.TrueForAll(Coreutils, t => File.Exists(ToolDll(t)));

    static (string stdout, int rc) RunBash(string script) => RunBash(script, registerExternals: false);

    static (string stdout, int rc) RunBash(string script, bool registerExternals)
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);
        if (registerExternals)                                  // M5: managed coreutil externals
            foreach (var t in Coreutils) table.RegisterTool(t, ToolDll(t));

        var sink = new BufferSinkHandle();
        var proc = table.CreateRoot(BashDll());
        proc.Fds.Set(0, new DevNullHandle());      // empty stdin — a process has fd0; heredocs/
                                                   // here-strings save+redirect it (else they bail)
        proc.Fds.Set(1, sink);
        proc.Fds.Set(2, new BufferSinkHandle());   // swallow shell-init chatter

        int rc = proc.Run(new[] { "bash", "--norc", "--noprofile", "-c", script });
        return (Encoding.UTF8.GetString(sink.ToArray()), rc);
    }

    [Fact]
    public void Bash_echo_runs_on_sandbox_pal()
    {
        if (!File.Exists(BashDll())) return;   // skip if the SandboxBash image isn't built
        var (stdout, rc) = RunBash("echo hi");
        Assert.Equal("hi\n", stdout);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void Bash_word_expansion_and_arithmetic()
    {
        if (!File.Exists(BashDll())) return;
        var (stdout, rc) = RunBash("x=world; echo \"hi $x $((6*7))\"");
        Assert.Equal("hi world 42\n", stdout);
        Assert.Equal(0, rc);
    }

    /// <summary>The M4.6 foundation: a green-process spawns the SAME bash image as a child running
    /// `-c '<body>'` with its stdout wired to a pipe the parent reads — the in-process analog of
    /// bash's comsub "re-exec self". Proves SpawnImage + Pipe + bash-on-bash before the bash-side
    /// re-pointing (SYS_spawn_self) lands.</summary>
    [Fact]
    public void Bash_on_bash_spawn_writes_to_pipe()
    {
        if (!File.Exists(BashDll())) return;

        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        var parent = table.CreateRoot(BashDll());
        var pipe = new Pipe();
        int rfd = parent.Fds.Add(new PipeReadHandle(pipe));
        int wfd = parent.Fds.Add(new PipeWriteHandle(pipe));

        int childPid = table.SpawnImage(BashDll(),
            new[] { "bash", "--norc", "--noprofile", "-c", "echo deep" },
            parent, new[] { (1, wfd) });        // child stdout (fd1) -> parent's pipe write end

        parent.Fds.Close(wfd);                  // parent done writing; child's copy closes on exit -> reader EOF
        int rc = table.Wait(childPid);

        var read = parent.Fds.Get(rfd);
        var buf = new byte[256];
        var sb = new StringBuilder();
        int n;
        while ((n = read.Read(buf)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));

        Assert.Equal("deep\n", sb.ToString());
        Assert.Equal(0, rc);
    }

    /// <summary>Command substitution on green-processes (`$(...)` re-pointed onto SYS_spawn_self),
    /// including nested comsub and function/var state transfer into the child. Run as a DIVERSE
    /// SEQUENCE so it also guards the cross-run regression: a prior green-process's collectible ALC
    /// must not be GC-unloaded mid-execution of a later one (fixed by collecting at Reset's safe
    /// point). Without that fix this sequence corrupts and the last case NRE's in the child.</summary>
    [Fact]
    public void Bash_command_substitution_sequence()
    {
        if (!File.Exists(BashDll())) return;
        Assert.Equal("x\n",      RunBash("echo $(echo x)").stdout);
        Assert.Equal("deep\n",   RunBash("echo $(echo $(echo deep))").stdout);
        Assert.Equal("abc\n",    RunBash("echo a$(echo b)c").stdout);
        Assert.Equal("PRE-X\n",  RunBash("p=PRE; tag(){ echo $p-$1; }; echo $(tag X)").stdout);
        Assert.Equal("hi bob\n", RunBash("greet(){ echo \"hi $1\"; }; echo $(greet bob)").stdout);
    }

    /// <summary>Subshells `( … )` on green-processes: a user subshell forks on native bash; on the
    /// PAL it spawns a fresh bash green-process (chibil_spawn_subshell) running the deparsed body
    /// with the parent's std fds inherited and functions/vars transferred, so output reaches the
    /// parent's stdout while variable/cwd changes stay isolated. Exercises isolation, exit status,
    /// function transfer, nesting, and the CMD_NO_FORK "last subshell" path.</summary>
    [Fact]
    public void Bash_subshells()
    {
        if (!File.Exists(BashDll())) return;
        Assert.Equal("sub\n",   RunBash("( echo sub )").stdout);                       // CMD_NO_FORK (last)
        Assert.Equal("2\n1\n",  RunBash("x=1; ( x=2; echo $x ); echo $x").stdout);     // var isolation
        Assert.Equal("3\n",     RunBash("( exit 3 ); echo $?").stdout);                // exit status
        Assert.Equal("hi bob\n",RunBash("greet(){ echo \"hi $1\"; }; ( greet bob )").stdout); // fn transfer
        Assert.Equal("deep\n",  RunBash("( ( ( echo deep ) ) )").stdout);             // nesting
        Assert.Equal("nested\n",RunBash("echo $( ( echo nested ) )").stdout);          // subshell in comsub
    }

    /// <summary>The `exit` builtin and `set -e` (ERREXIT) — both do `jump_to_top_level(top_level)`
    /// from a top-level simple command, landing in parse_and_execute's `setjmp` cleanup. chibil's
    /// whole-body setjmp lowering used to RE-RUN parse_and_execute's pre-loop `begin_unwind_frame`
    /// on resume, duplicating the unwind-protect list → a double-free crash. Fixed by hoisting the
    /// branch-free pre-setjmp prefix out of the setjmp try region (CodeGen EmitSetjmpWrappedBody).</summary>
    [Fact]
    public void Bash_exit_builtin_and_errexit()
    {
        if (!File.Exists(BashDll())) return;
        Assert.Equal(7, RunBash("exit 7").rc);
        Assert.Equal(0, RunBash("exit 0").rc);
        Assert.Equal(5, RunBash("x=1; exit 5").rc);
        var (so, rc) = RunBash("echo hi; exit 3");
        Assert.Equal("hi\n", so);
        Assert.Equal(3, rc);
        var (eso, erc) = RunBash("set -e; false; echo no");   // ERREXIT longjmp, same cleanup path
        Assert.Equal("", eso);
        Assert.Equal(1, erc);
    }

    /// <summary>M5 walking skeleton: a real external command (`cat`, a managed musl coreutil
    /// green-process) resolved and executed BY bash. Exercises the full disk-command path on the
    /// PAL: PATH search via statx tool-discovery, the executable-bit check via faccessat, dropping
    /// the CMD_NO_FORK in-place-exec optimization (no execve on the PAL), spawning the tool with
    /// stdio inherited (SYS_spawn_tool), and — the subtle one — fcntl(F_DUPFD) so bash's redirect
    /// save/restore over stdout doesn't permanently close it before the spawn. cat reads files from
    /// the shared vfs that the preceding redirections created.</summary>
    [Fact]
    public void Bash_runs_cat_external_coreutil()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("hi\n",   RunBash("echo hi > /f.txt; cat /f.txt", registerExternals: true).stdout);
        Assert.Equal("a\nb\n", RunBash("echo a > /a; echo b > /b; cat /a /b", registerExternals: true).stdout);
        // redirect over stdout then a clean external: stdout must survive the redirect save/restore
        Assert.Equal("after\n", RunBash("echo x > /q; echo after", registerExternals: true).stdout);
    }

    /// <summary>M5: a second managed coreutil (`wc`) — the cat pattern applied to a stdin-consuming
    /// tool — counting lines/words/bytes of files and of stdin. Confirms the tool-registry path
    /// generalizes beyond cat.</summary>
    [Fact]
    public void Bash_runs_wc_external_coreutil()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("2 /d\n",     RunBash("printf 'a b\\nc\\n' > /d; wc -l /d", registerExternals: true).stdout);
        Assert.Equal("3 /e\n",     RunBash("printf 'a b c\\n' > /e; wc -w /e", registerExternals: true).stdout);
        Assert.Equal("1 3 6 /e\n", RunBash("printf 'a b c\\n' > /e; wc /e", registerExternals: true).stdout);
    }

    /// <summary>M5: real pipelines whose stages are managed green-process externals. The M4.6
    /// builtin|builtin-group pipeline deadlocked (pipe handoff broken); with externals spawning as
    /// green-processes — each stage a process with the pipe ends inherited via fdMap — the handoff
    /// works. Covers external|external, builtin|external, and a 3-stage external chain, plus
    /// cat|wc (the canonical "stream into a counter").</summary>
    [Fact]
    public void Bash_pipelines_with_externals()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("hi\n",  RunBash("echo hi > /f.txt; cat /f.txt | cat", registerExternals: true).stdout);
        Assert.Equal("piped\n", RunBash("echo piped | cat", registerExternals: true).stdout);
        Assert.Equal("a\nb\n", RunBash("printf 'a\\nb\\n' > /ab; cat /ab | cat | cat", registerExternals: true).stdout);
        Assert.Equal("3\n",   RunBash("printf 'x\\ny\\nz\\n' > /c; cat /c | wc -l", registerExternals: true).stdout);
    }

    /// <summary>M5 (make_child gap): a pipeline STAGE that isn't a plain registered external —
    /// a `{ }` brace group, a `( )` subshell, a `while`/control structure, or even a builtin like
    /// `read` — has no fork on the PAL and used to NRE (make_child). Now each such synchronous
    /// stage is deparsed and spawned as a fresh bash green-process on the pipe ends
    /// (chibil_spawn_subshell). The `echo X | { read y; … }` case is the original M4.6 deadlock.</summary>
    [Fact]
    public void Bash_compound_pipeline_stages()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("inbrace\n", RunBash("echo inbrace | { cat; }", registerExternals: true).stdout);
        Assert.Equal("3\n",       RunBash("printf '1\\n2\\n3\\n' | { wc -l; } | { cat; }", registerExternals: true).stdout);
        Assert.Equal("GOT-hi\n",  RunBash("echo hi | { read y; echo \"GOT-$y\"; }", registerExternals: true).stdout);
        Assert.Equal("2\n",       RunBash("printf 'a\\nb\\n' | while read x; do echo \"$x\"; done | wc -l", registerExternals: true).stdout);
        Assert.Equal("sub\n",     RunBash("echo sub | ( cat )", registerExternals: true).stdout);
        // external carrying a redirect AS a pipe stage (the chibil_spawn_tool branch is skipped
        // for redirect+pipe, so this goes through the green-process spawn too)
        Assert.Equal("OK\n",      RunBash("printf 'x\\ny\\n' | grep x >/dev/null && echo OK", registerExternals: true).stdout);
    }

    /// <summary>M5 (make_child gap): an unknown command AS a pipeline stage no longer NREs — it's
    /// spawned as a bash green-process that reports "command not found" (exit 127) and drains its
    /// pipe end, and the parent shell continues.</summary>
    [Fact]
    public void Bash_unknown_command_in_pipeline()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("after\n", RunBash("echo hi | nosuchcmd; echo after", registerExternals: true).stdout);
        Assert.Equal("127\n",   RunBash("echo hi | nosuchcmd; echo $?", registerExternals: true).stdout);
    }

    /// <summary>M5 (async `&` / background jobs): a `&` command spawns a background bash
    /// green-process the parent does NOT wait for; `wait` reaps it, `$!` is its pid. No fork on
    /// the PAL — intercepted in execute_connection's `&` case, deparsing the command WITHOUT the
    /// `&` (deparsing the connection re-backgrounds infinitely and hard-crashes the host). Exercises
    /// true parent/child green-process concurrency (parent doesn't block until `wait`).</summary>
    [Fact]
    public void Bash_background_jobs()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("hi\n",      RunBash("echo hi & wait").stdout);
        Assert.Equal("bg\nend\n",  RunBash("echo bg & wait; echo end").stdout);
        Assert.Equal("a\nb\n",    RunBash("{ echo a; echo b; } & wait").stdout);   // compound bg
        // state transfer into the bg job (vars/functions)
        Assert.Equal("v=5\n",     RunBash("x=5; echo \"v=$x\" & wait").stdout);
        // $! is the backgrounded pid; wait on it returns 0
        Assert.Equal("ok\n",      RunBash("echo x >/dev/null & wait $! && echo ok").stdout);
        // several sequential background jobs, each waited (concurrency)
        Assert.Equal("1\n2\n3\n", RunBash("echo 1 & wait; echo 2 & wait; echo 3 & wait").stdout);
    }

    /// <summary>fd-duplicating redirections: `>&2` (write to stderr), `2>&1` (merge stderr into
    /// stdout), and the order-sensitive `>file 2>&1`. Exercises dup2 over the fd table.</summary>
    [Fact]
    public void Bash_stderr_redirection()
    {
        if (!File.Exists(BashDll())) return;
        // 2>&1 merges stderr into stdout (RunBash captures fd1; fd2 is the swallow sink)
        Assert.Equal("out\nerr\n", RunBash("{ echo out; echo err >&2; } 2>&1").stdout);
        Assert.Equal("hi\n",       RunBash("echo hi 2>&1").stdout);
        // a bare `>&2` line goes to stderr, so it must NOT appear on captured stdout
        Assert.Equal("o\n",        RunBash("echo o; echo e >&2").stdout);
        // order: `>/dev/null 2>&1` sends both streams to the bit bucket -> nothing captured
        Assert.Equal("",           RunBash("{ echo x; echo y >&2; } >/dev/null 2>&1").stdout);
        Assert.Equal("done\n",     RunBash("{ echo x; echo y >&2; } >/dev/null 2>&1; echo done").stdout);
    }

    /// <summary>Here-documents (`<<`) and here-strings (`<<<`): bash builds a small one from a
    /// PIPE (writes the document, closes the writer, redirects the read end onto fd0). Requires the
    /// process to HAVE an fd0 to save/restore — the root green-process is seeded with empty stdin.</summary>
    [Fact]
    public void Bash_heredoc_herestring()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("hs\n",      RunBash("cat <<< hs", registerExternals: true).stdout);
        Assert.Equal("rv\n",      RunBash("read v <<< rv; echo $v").stdout);          // builtin reader
        Assert.Equal("L1\nL2\n",  RunBash("cat <<EOF\nL1\nL2\nEOF", registerExternals: true).stdout);
        Assert.Equal("x=5\n",     RunBash("cat <<EOF\nx=$((2+3))\nEOF", registerExternals: true).stdout); // expansion
        Assert.Equal("raw $x\n",  RunBash("cat <<'EOF'\nraw $x\nEOF", registerExternals: true).stdout);    // quoted: no expansion
    }

    /// <summary>`source` / `.` runs a script file's commands in the current shell. The file is
    /// in the VFS; bash's _evalfile opens it and fstat()s the fd — on musl that goes through
    /// statx with AT_EMPTY_PATH, which must stat the FD (not resolve the empty path as the cwd
    /// directory, which made source reject every file as "is a directory").</summary>
    [Fact]
    public void Bash_source_dot()
    {
        if (!File.Exists(BashDll())) return;
        Assert.Equal("src\n", RunBash("echo 'echo src' > /s; . /s").stdout);
        Assert.Equal("src\n", RunBash("echo 'echo src' > /s; source /s").stdout);
        // sourced file runs in the current shell: it can set vars the caller sees
        Assert.Equal("v=7\n", RunBash("echo 'x=7' > /lib; . /lib; echo \"v=$x\"").stdout);
        // and define functions
        Assert.Equal("hi bob\n", RunBash("echo 'g(){ echo \"hi $1\"; }' > /f; . /f; g bob").stdout);
    }

    /// <summary>/dev/null (and /dev/zero) are character devices, not stored files: writes to
    /// /dev/null are discarded, reads hit EOF; /dev/zero reads zeros. Backs the ubiquitous
    /// `… >/dev/null`, including as a redirect on a pipeline stage.</summary>
    [Fact]
    public void Bash_dev_null()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("after\n", RunBash("echo discarded >/dev/null; echo after").stdout);
        Assert.Equal("", RunBash("cat /dev/null").stdout);                          // reads EOF
        Assert.Equal("yes\n", RunBash("echo data | { grep data >/dev/null && echo yes; }", registerExternals: true).stdout);
        Assert.Equal("ok\n", RunBash("[ -e /dev/null ] && echo ok").stdout);        // stat sees it
    }

    /// <summary>M5: the coreutil batch — true/false (exit status), head/tail (line slicing, incl. a
    /// 3-stage `head|tail` external pipeline), and ls (directory listing via the getdents/readdir
    /// path, sorted, hidden-filtered).</summary>
    [Fact]
    public void Bash_coreutil_batch()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("yes\n", RunBash("true && echo yes", registerExternals: true).stdout);
        Assert.Equal("no\n",  RunBash("false || echo no", registerExternals: true).stdout);
        Assert.Equal("1\n2\n", RunBash("printf '1\\n2\\n3\\n' > /n; head -2 /n", registerExternals: true).stdout);
        Assert.Equal("2\n3\n", RunBash("printf '1\\n2\\n3\\n' > /n; tail -2 /n", registerExternals: true).stdout);
        Assert.Equal("mid\n",  RunBash("printf 'a\\nmid\\nz\\n' | head -2 | tail -1", registerExternals: true).stdout);
        // "etc" is always present — the kernel seeds /etc/{passwd,group} as the virtual user DB.
        Assert.Equal("a\nb\nc\netc\n", RunBash("echo x>/b; echo x>/a; echo x>/c; ls /", registerExternals: true).stdout);
    }

    /// <summary>M5: the text-processing coreutils — `sort` (whole-input transform: lexicographic,
    /// -r reverse, -n numeric, -u unique) and `grep` (-v invert, -i fold case, -c count, -n line
    /// numbers) — and the canonical multi-stage pipelines they compose (`grep | sort | head`,
    /// `sort -u | wc -l`).</summary>
    [Fact]
    public void Bash_text_pipeline_coreutils()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("a\nb\nc\n", RunBash("printf 'c\\na\\nb\\n' | sort", registerExternals: true).stdout);
        Assert.Equal("1\n2\n10\n", RunBash("printf '10\\n1\\n2\\n' | sort -n", registerExternals: true).stdout);
        Assert.Equal("2:two\n",   RunBash("printf 'one\\ntwo\\nthree\\n' | grep -n two", registerExternals: true).stdout);
        Assert.Equal("2\n",       RunBash("printf 'foo\\nbar\\nfoobar\\n' | grep -c foo", registerExternals: true).stdout);
        // 4-stage pipeline of externals: grep filters, sort orders, head slices
        Assert.Equal("apple\n",   RunBash("printf 'apple\\nbanana\\napricot\\n' | grep ap | sort | head -1", registerExternals: true).stdout);
        Assert.Equal("3\n",       RunBash("printf 'b\\na\\nb\\nc\\na\\n' | sort -u | wc -l", registerExternals: true).stdout);
    }

    /// <summary>M5: REAL POSIX regex — musl's TRE engine (src/regex) is now compiled into the
    /// managed set, so grep matches regular expressions (anchors, char classes, BRE alternation)
    /// and bash's `[[ =~ ]]` works for real (it used to always fail against the regcomp stub).
    /// This is what the chibil braced-string-array codegen fix unblocked.</summary>
    [Fact]
    public void Bash_real_regex()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        // grep: anchor + char class + BRE alternation
        Assert.Equal("apple\napricot\n", RunBash("printf 'apple\\nbanana\\napricot\\n' | grep '^a'", registerExternals: true).stdout);
        Assert.Equal("a1c\na2c\n",       RunBash("printf 'a1c\\nabc\\na2c\\n' | grep 'a[0-9]c'", registerExternals: true).stdout);
        Assert.Equal("3\n",              RunBash("printf 'cat\\ncar\\ncot\\n' | grep -c 'c.t\\|car'", registerExternals: true).stdout);
        // bash [[ =~ ]] — real ERE matching (was always-false against the stub)
        Assert.Equal("match\n",   RunBash("[[ hello123 =~ [0-9]+ ]] && echo match", registerExternals: true).stdout);
        Assert.Equal("nomatch\n", RunBash("[[ hello =~ [0-9]+ ]] || echo nomatch", registerExternals: true).stdout);
    }

    /// <summary>M5: virtualized user DB. musl's getpwnam/getpwuid read /etc/passwd via the VFS,
    /// which the kernel seeds with a default root + sandbox user — so bash's `~user` tilde
    /// expansion resolves against the virtual passwd (no host user DB, no stubs). This is what
    /// adding src/passwd (real getpw*) on top of the regex work enabled.</summary>
    [Fact]
    public void Bash_virtual_userdb_tilde_expansion()
    {
        if (!File.Exists(BashDll())) return;
        Assert.Equal("/root\n",         RunBash("echo ~root").stdout);
        Assert.Equal("/home/sandbox\n", RunBash("echo ~sandbox").stdout);
        Assert.Equal("yes\n",           RunBash("[ \"$(echo ~root)\" = /root ] && echo yes").stdout);
    }

    /// <summary>M5 robustness: an unknown command reports "command not found" with $?==127 and the
    /// shell CONTINUES — without fork (make_child has no green form) and without exiting in-process
    /// (which would wrongly kill the shell mid-script). Handled in the parent for the no-pipe case.
    /// (Known gap, deferred: an unknown command AS a pipeline stage still goes through make_child.)</summary>
    [Fact]
    public void Bash_unknown_command_is_127_and_continues()
    {
        if (!File.Exists(BashDll()) || !ExternalsBuilt()) return;
        Assert.Equal("127\n",  RunBash("frobnicate; echo $?", registerExternals: true).stdout);
        Assert.Equal("cont\n", RunBash("nosuchcmd; echo cont", registerExternals: true).stdout);
        // a real external after a not-found still runs (shell state intact)
        Assert.Equal("ok\n",   RunBash("nope; echo ok > /o; cat /o", registerExternals: true).stdout);
    }

    /// <summary>Deeply-NESTED command substitution: recursive `fact 5` (5 levels of comsub, each a
    /// nested green-process spawned while its parent blocks waiting) and a 6-deep linear nest. This
    /// is the stress case for the collectible-ALC lifecycle fix — many green-process ALCs alive at
    /// once; without keeping them referenced until fully idle, a background-GC unload mid-execution
    /// corrupts a child's heap. Run repeatedly to exercise cross-run ALC reclamation too.</summary>
    [Fact]
    public void Bash_deeply_nested_comsub()
    {
        if (!File.Exists(BashDll())) return;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("120\n", RunBash("fact(){ if [ $1 -le 1 ]; then echo 1; else echo $(($1*$(fact $(($1-1))))); fi; }; fact 5").stdout);
            Assert.Equal("deep\n", RunBash("echo $(echo $(echo $(echo $(echo $(echo deep)))))").stdout);
        }
    }

    /// <summary>The reap path comsub needs: a parent green-process reaps a spawned bash child via
    /// waitpid(-1) (SandboxPal SYS_wait4 → ProcessTable.WaitPid), recovering its exit status.
    /// WaitPid attributes the child to the caller via the kernel's current pid, so we enter the
    /// parent's context (as bash's waitpid syscall would).</summary>
    [Fact]
    public void Waitpid_reaps_green_child_with_status()
    {
        if (!File.Exists(BashDll())) return;

        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        var parent = table.CreateRoot(BashDll());
        // `false` exits with status 1 via main's normal return (the `exit` builtin's
        // longjmp-unwind path has a separate musl-free bug — tracked for M4.6).
        int child = table.SpawnImage(BashDll(),
            new[] { "bash", "--norc", "--noprofile", "-c", "false" },
            parent, System.Array.Empty<(int, int)>());

        SandboxPal.EnterProcess(parent.Pid);     // act as the parent (as the waitpid syscall would)
        try
        {
            int reaped = table.WaitPid(-1, noHang: false, out int code);
            Assert.Equal(child, reaped);
            Assert.Equal(1, code);
            // second reap: no more children -> ECHILD (-1)
            Assert.Equal(-1, table.WaitPid(-1, noHang: false, out _));
        }
        finally { SandboxPal.EnterProcess(0); }
    }
}
