using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LinkerOutputTests
{
    // -shared: a library with no `main` links, the PE is marked DLL, and there is
    // no managed entry point.
    [Fact]
    public void Shared_library_links_without_main_and_has_no_entry_point()
    {
        byte[] obj = TestCompiler.CompileToObj("int add(int a, int b){ return a + b; }\n", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "lib.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), shared: true, assemblyName: "mylib");

        using var pr = new PEReader(ImmutableArray.Create(pe));
        Assert.Equal(0, pr.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress);
        Assert.True((pr.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0);
        var md = pr.GetMetadataReader();
        Assert.Equal("mylib", md.GetString(md.GetAssemblyDefinition().Name));
    }

    // An executable keeps its managed entry point, and the assembly identity follows
    // the supplied name (CLI derives it from -o).
    [Fact]
    public void Executable_keeps_entry_point_and_identity_from_name()
    {
        byte[] obj = TestCompiler.CompileToObj("int main(void){ return 0; }\n", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "exe.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), assemblyName: "myexe");

        using var pr = new PEReader(ImmutableArray.Create(pe));
        Assert.NotEqual(0, pr.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress);
        var md = pr.GetMetadataReader();
        Assert.Equal("myexe", md.GetString(md.GetAssemblyDefinition().Name));
    }

    // -e/--entry selects a non-default entry symbol; the default 'main' lookup fails
    // when there is no main.
    [Fact]
    public void Custom_entry_symbol_is_used()
    {
        byte[] obj = TestCompiler.CompileToObj("int go(void){ return 0; }\n", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "e.obj");

        var ex = Assert.Throws<LinkException>(() => LinkPipeline.LinkToBytes(new[] { of }, new List<string>()));
        Assert.Contains("main", ex.Message);

        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), entrySymbol: "go");
        Assert.NotEmpty(pe);
    }

    // int main(int, char**, char**) gets argc/argv/envp marshalling helpers wired
    // into the entry; envp is the process environment as KEY=VALUE C strings (verified
    // at runtime in the vssmoke harness; here we check the helpers are synthesized).
    [Fact]
    public void Main_with_envp_synthesizes_environment_marshalling()
    {
        byte[] obj = TestCompiler.CompileToObj(
            "int main(int argc, char** argv, char** envp){ return envp[0] != 0; }\n",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "envp.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());

        using var pr = new PEReader(ImmutableArray.Create(pe));
        var md = pr.GetMetadataReader();
        var names = md.MethodDefinitions
            .Select(h => md.GetString(md.GetMethodDefinition(h).Name)).ToHashSet();
        Assert.Contains("__chibil_make_envp", names);
        Assert.Contains("__chibil_make_argv", names);
        Assert.Contains("__chibil_argc", names);
    }
}
