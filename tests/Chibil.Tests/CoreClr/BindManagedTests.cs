using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class BindManagedTests
{
    [Fact]
    public void Reads_assembly_identity_from_corelib()
    {
        // System.Private.CoreLib is always present; read its identity.
        string corelib = typeof(object).Assembly.Location;
        var id = ManagedReference.Read(corelib);
        Assert.Equal("System.Private.CoreLib", id.Name);
        Assert.True(id.Version.Major >= 8);
        Assert.NotEmpty(id.PublicKeyToken);   // corelib is strong-named
    }
}
