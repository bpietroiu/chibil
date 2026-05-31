using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Tests for bugs identified in the phase-1 audit of the MSIL backend.
/// </summary>
public class AuditBugTests : ChibiTestBase
{
    [Fact]
    public void StmtExprValuePreserved()
    {
        Compile("""
            int main() {
                int x = ({ int a = 5; a + 10; });
                return x;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 15);
    }

    [Fact]
    public void FloatCondition()
    {
        Compile("""
            int main() {
                double d = 1.5;
                if (d) return 42;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void NotPointer()
    {
        Compile("""
            int main() {
                int x = 5;
                int *p = &x;
                if (!p) return 1;
                p = 0;
                if (!p) return 0;
                return 2;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void NotLongLong()
    {
        Compile("""
            int main() {
                long long x = 1;
                if (!x) return 1;
                x = 0;
                if (!x) return 0;
                return 2;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void TlsRejected()
    {
        CompileExpectingError("""
            _Thread_local int tls_var;
            int main() { return tls_var; }
            """)
        .AssertErrorContains("thread");
    }

    [Fact]
    public void CastToVoid()
    {
        Compile("""
            int side_effect(void) { return 42; }
            int main() {
                (void)side_effect();
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void CastToVoidOnVoidCall()
    {
        Compile("""
            void do_nothing(void) {}
            int main() {
                (void)do_nothing();
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void UnsignedInt32ToFloat()
    {
        Compile("""
            int main() {
                unsigned int big = 4294967295U;
                double d = (double)big;
                if (d > 4.2e9 && d < 4.3e9) return 0;
                return 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void AddrOfFunction()
    {
        Compile("""
            int add(int a, int b) { return a + b; }
            int apply(int (*fn)(int,int), int x, int y) { return fn(x,y); }
            int main() { return apply(&add, 10, 3); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 13);
    }

    [Fact]
    public void FloatLeNaN()
    {
        Compile("""
            int main() {
                double nan = 0.0 / 0.0;
                if (nan <= 1.0) return 1;
                if (1.0 <= nan) return 2;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void UnsignedInt64ToFloat()
    {
        Compile("""
            int main() {
                unsigned long long big = 18000000000000000000ULL;
                double d = (double)big;
                if (d > 17e18 && d < 19e18) return 0;
                return 1;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void VariadicDefNowCompiles()
    {
        // Previously chibil rejected variadic function definitions ("variadic function
        // definitions are not supported in MSIL mode"). They are now supported, lowered
        // via the va-buffer ABI (hidden trailing pointer param). Compiling must succeed;
        // end-to-end behaviour is covered by VarargsTests on the CoreCLR target.
        // Compile() throws ChibiException on failure; reaching here means it compiled.
        Compile("""
            int my_sum(int n, ...) {
                __builtin_va_list ap; __builtin_va_start(ap, n);
                int s = 0; for (int i = 0; i < n; i++) s += __builtin_va_arg(ap, int);
                __builtin_va_end(ap); return s;
            }
            int main() { return 0; }
            """);
    }
}
