using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public unsafe class SignalTests
{
    [Fact]
    public void Sigaction_and_sigprocmask_succeed_as_stubs()
    {
        SandboxPal.Reset();
        SandboxPal.EnterProcess(5);

        // bash installs handlers (rt_sigaction) and masks signals (rt_sigprocmask) during
        // startup; in non-interactive mode these only need to succeed. Delivery is deferred.
        Assert.Equal(0, SandboxPal.Syscall(13 /*rt_sigaction*/, 2 /*SIGINT*/, 0, 0, 8, 0, 0));

        // rt_sigprocmask(how, set, oldset, sigsetsize): when oldset is non-NULL the kernel must
        // write the (empty) previous mask there so bash reads a sane value back.
        ulong oldset = 0xDEADBEEF;
        Assert.Equal(0, SandboxPal.Syscall(14 /*rt_sigprocmask*/, 0 /*SIG_BLOCK*/, 0,
                                           (long)(nint)(&oldset), 8 /*sigsetsize*/, 0, 0));
        Assert.Equal(0UL, oldset);   // cleared to the empty mask
    }
}
