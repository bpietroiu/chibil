using System.Threading.Tasks;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class GreenProcessTests
{
    [Fact]
    public void Pal_reports_and_reads_back_per_pid()
    {
        SandboxPal.Reset();
        SandboxPal.EnterProcess(7);
        // syscall 0x1000 = report a1 as this pid's value
        long rc = SandboxPal.Syscall(0x1000, 42, 0, 0, 0, 0, 0);
        Assert.Equal(0, rc);
        Assert.Equal(42, SandboxPal.GetReport(7));
        Assert.Equal(1, SandboxPal.ReportCount);
    }

    [Fact]
    public void Counter_tool_builds_and_is_a_loadable_assembly()
    {
        string dll = SandboxToolBuilder.Build("counter");
        Assert.True(System.IO.File.Exists(dll));
        Assert.True(new System.IO.FileInfo(dll).Length > 0);
    }

    [Fact]
    public void Concurrent_green_processes_isolate_statics_and_share_kernel()
    {
        SandboxPal.Reset();
        string dll = SandboxToolBuilder.Build("counter");

        var p1 = new GreenProcess(pid: 1, toolDllPath: dll);
        var p2 = new GreenProcess(pid: 2, toolDllPath: dll);

        // Run both at once. If the tool's static `counter` were SHARED (D6 false), the two
        // 1,000,000-increment loops would race on one variable and neither would report
        // exactly 1,000,000. With ALC isolation, each instance has its own `counter`.
        Task t1 = Task.Run(() => p1.Run(new[] { "counter" }));
        Task t2 = Task.Run(() => p2.Run(new[] { "counter" }));
        Task.WaitAll(t1, t2);

        Assert.Equal(1_000_000, SandboxPal.GetReport(1));   // isolation
        Assert.Equal(1_000_000, SandboxPal.GetReport(2));   // isolation
        Assert.Equal(2, SandboxPal.ReportCount);            // both hit the SAME kernel dict
    }

    [Fact]
    public void Green_process_can_spawn_and_wait_a_child()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        // Register tool id 1 = counter (built in-process).
        table.RegisterTool(id: 1, toolDllPath: SandboxToolBuilder.Build("counter"));
        SandboxPal.AttachProcessTable(table);

        string spawnerDll = SandboxToolBuilder.Build("spawner");
        var spawner = table.CreateRoot(spawnerDll);   // gets pid, registers in table
        int rc = spawner.Run(new[] { "spawner" });

        Assert.Equal(0, rc);
        // counter (the child) reported 1,000,000 under its own pid; spawner reported the child's pid.
        int childPid = (int)SandboxPal.GetReport(spawner.Pid);
        Assert.Equal(1_000_000, SandboxPal.GetReport(childPid));
        Assert.NotEqual(spawner.Pid, childPid);
    }

    [Fact]
    public void Green_process_alc_unloads_after_run()
    {
        SandboxPal.Reset();
        string dll = SandboxToolBuilder.Build("counter");

        System.WeakReference weak = RunAndGetAlcWeakRef(dll);   // separate method so locals can be GC'd

        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "the tool's AssemblyLoadContext did not unload");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static System.WeakReference RunAndGetAlcWeakRef(string dll)
    {
        var gp = new GreenProcess(pid: 99, toolDllPath: dll);
        gp.Run(new[] { "counter" });
        System.WeakReference w = gp.Unload();   // returns a weak ref to the ALC, then releases it
        return w;
    }
}
