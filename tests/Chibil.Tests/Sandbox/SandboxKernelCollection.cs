using Xunit;

namespace Chibil.Tests.Sandbox;

// SandboxPal is a process-wide singleton kernel (the __chibil_syscall bind targets a static
// method). In production each sandbox is its own OS process, so one kernel is correct; in the
// test process, classes that drive the kernel's static state must run serially, not in xUnit's
// default per-class parallelism. Classes annotated [Collection("SandboxKernel")] share this
// collection and therefore run one at a time.
[CollectionDefinition("SandboxKernel", DisableParallelization = true)]
public class SandboxKernelCollection { }
