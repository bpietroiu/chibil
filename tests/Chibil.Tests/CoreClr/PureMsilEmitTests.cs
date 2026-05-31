using Chibil;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PureMsilEmitTests
{
    [Fact]
    public void Default_target_is_ijw()
    {
        var opts = new CompilerOptions();
        Assert.Equal(TargetProfile.Ijw, opts.Target);
    }
}
