/* The green-process analog of a shell pipeline stage: create a pipe, spawn a child whose
 * stdout (fd 1) is wired to the pipe's write end, drain the pipe to EOF, wait the child,
 * and report the total bytes read. Exercises spawn-with-fd-inheritance + pipe EOF + wait. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    int fds[2];                                  /* pipe2 writes int pipefd[2] */
    if (__chibil_syscall(293 /*pipe2*/, (long long)fds, 0, 0, 0, 0, 0) != 0)
        { __chibil_syscall(0x1000, -1, 0, 0, 0, 0, 0); return 1; }
    int rfd = fds[0], wfd = fds[1];

    /* spawn producer (tool id 2): map the child's fd 1 onto this process's write end.
     * fdmap is flat pairs {childFd, parentFd}; one pair here. */
    int fdmap[2] = { 1 /*child fd 1*/, wfd /*<- parent wfd*/ };
    long long child = __chibil_syscall(0x1001 /*spawn*/, 2 /*producer*/,
                                       (long long)fdmap, 1 /*pairs*/, 0, 0, 0);
    if (child < 0) { __chibil_syscall(0x1000, -2, 0, 0, 0, 0, 0); return 2; }

    /* Close our own write end so the pipe reports EOF once the child finishes writing. */
    __chibil_syscall(3 /*close*/, wfd, 0, 0, 0, 0, 0);

    /* Drain the pipe until EOF (read returns 0). */
    char buf[4096];
    long long total = 0, n;
    while ((n = __chibil_syscall(0 /*read*/, rfd, (long long)buf, sizeof buf, 0, 0, 0)) > 0)
        total += n;

    __chibil_syscall(0x1002 /*wait*/, child, 0, 0, 0, 0, 0);
    __chibil_syscall(0x1000 /*report*/, total, 0, 0, 0, 0, 0);
    return 0;
}
