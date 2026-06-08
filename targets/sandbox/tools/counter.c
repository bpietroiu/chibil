/* Freestanding test tool: increment a file-scope static N times, then report it via the
 * sandbox kernel. No libc — only the __chibil_syscall bind. The static `counter` is what
 * chibil emits as a .NET static field; the green-process ALC test proves it is isolated. */
extern long __chibil_syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6);

static long counter;            /* the static under test */

int main(int argc, char **argv)
{
    long n = 1000000;           /* large enough that shared (racy) increments would diverge */
    for (long i = 0; i < n; i++) counter++;
    __chibil_syscall(0x1000 /* report */, counter, 0, 0, 0, 0, 0);
    return 0;
}
