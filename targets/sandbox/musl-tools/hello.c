/* A real managed-musl program: printf goes through musl stdio (malloc + buffering +
 * writev) on top of SandboxPal. Proves the full libc hosts on the sandbox kernel. */
#include <stdio.h>

int main(void)
{
    printf("hello, sandbox\n");
    return 0;
}
