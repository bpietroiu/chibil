/* Freestanding test tool: increment a file-scope static N times, then report it via the
 * sandbox kernel. No libc — only the __chibil_syscall bind. The static `counter` is what
 * chibil emits as a .NET static field; the green-process ALC test proves it is isolated.
 *
 * Uses `long long` (guaranteed 64-bit) so the syscall ABI is Int64 — matching
 * SandboxPal.Syscall(long ...) — regardless of the data model. The real managed-musl
 * tools get the same 64-bit ABI by compiling with -mlp64 (where `long` is 64-bit). */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

static long long counter;       /* the static under test */

int main(int argc, char **argv)
{
    long long n = 1000000;      /* large enough that shared (racy) increments would diverge */
    for (long long i = 0; i < n; i++) counter++;
    __chibil_syscall(0x1000 /* report */, counter, 0, 0, 0, 0, 0);
    return 0;
}
