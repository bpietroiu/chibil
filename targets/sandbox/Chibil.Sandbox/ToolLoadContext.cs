using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Chibil.Sandbox
{
    /// <summary>
    /// Per-green-process load context. The tool assembly is loaded into THIS context
    /// (so its .NET static fields — the C globals — are private to this green-process).
    /// Everything the tool references but does not define (Chibil.Sandbox, the framework)
    /// is left to fall back to the Default context, so the SandboxPal kernel is a single
    /// shared instance across all green-processes.
    /// </summary>
    internal sealed class ToolLoadContext : AssemblyLoadContext
    {
        public ToolLoadContext(string name) : base(name, isCollectible: true) { }

        // Return null for everything: the runtime then resolves via the Default context,
        // which shares Chibil.Sandbox + framework. The tool itself is loaded explicitly
        // via LoadFromAssemblyPath (in GreenProcess), which places it in THIS context.
        protected override Assembly Load(AssemblyName assemblyName) => null;
    }
}
