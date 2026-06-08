/* Spawns the `counter` tool (id 1 in the kernel's tool registry), waits for it, then
 * reports the spawned child's pid so the test can confirm spawn+wait worked.
 * Uses `long long` for the 64-bit syscall ABI (see counter.c). */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    long long child = __chibil_syscall(0x1001 /* spawn */, 1 /* tool id: counter */, 0, 0, 0, 0, 0);
    __chibil_syscall(0x1002 /* wait  */, child, 0, 0, 0, 0, 0);
    __chibil_syscall(0x1000 /* report*/, child, 0, 0, 0, 0, 0);
    return 0;
}
