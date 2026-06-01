/* samples/sqlite/include/chibil_os.h
 * Cross-OS native file I/O decls for the chibil disk VFS. The linker routes each
 * symbol to libc.so.6 (Linux) or kernel32.dll (Windows) via --pinvoke; the C picks
 * the right one at runtime via __chibil_os_is_windows(). x64 only. */
#ifndef CHIBIL_OS_H
#define CHIBIL_OS_H

extern int __chibil_os_is_windows(void);

/* ---- POSIX (libc) ---- */
#define O_RDONLY 0
#define O_WRONLY 1
#define O_RDWR   2
#define O_CREAT  0100   /* octal 0100 = 64 (Linux x86-64) */
#define O_EXCL   0200
#define O_TRUNC  01000  /* octal 01000 = 512 */
#define SEEK_SET 0
#define SEEK_END 2
#define F_OK     0
extern int  open(const char *path, int flags, int mode);   /* declared non-variadic: always pass mode */
extern long long pread(int fd, void *buf, unsigned long long n, long long off);
extern long long pwrite(int fd, const void *buf, unsigned long long n, long long off);
extern int  ftruncate(int fd, long long len);
extern int  fsync(int fd);
extern int  close(int fd);
extern int  unlink(const char *path);
extern int  access(const char *path, int mode);
extern long long lseek(int fd, long long off, int whence);

/* ---- POSIX file locking (libc fcntl) ---- */
#define F_RDLCK 0
#define F_WRLCK 1
#define F_UNLCK 2
#define F_SETLK 6
/* struct flock, Linux x86-64 layout (32 bytes): l_type@0, l_whence@2, [pad@4],
   l_start@8, l_len@16, l_pid@24, [pad@28]. The natural 4-byte pad after l_whence
   aligns the 8-byte l_start — declared in field order, the compiler inserts it. */
struct flock {
    short l_type;
    short l_whence;
    long long l_start;
    long long l_len;
    int l_pid;
};
extern int fcntl(int fd, int cmd, void *arg);   /* non-variadic: arg is always &flock here */
extern int usleep(unsigned int usec);           /* libc */

/* ---- Win32 (kernel32) ---- */
#define GENERIC_READ          0x80000000u
#define GENERIC_WRITE         0x40000000u
#define FILE_SHARE_READ       0x00000001u
#define FILE_SHARE_WRITE      0x00000002u
#define CREATE_NEW            1
#define OPEN_EXISTING         3
#define OPEN_ALWAYS           4
#define FILE_ATTRIBUTE_NORMAL 0x80u
#define FILE_BEGIN            0
#define INVALID_FILE_ATTRIBUTES 0xFFFFFFFFu
#define ERROR_HANDLE_EOF      38   /* a positioned ReadFile past EOF returns FALSE with this */

/* OVERLAPPED, x64 layout (32 bytes). We set Offset/OffsetHigh from a 64-bit offset.
   (The real Win32 struct has a `PVOID Pointer` union arm overlapping Offset/OffsetHigh
   at bytes 16-23; omitted here since we only use the Offset/OffsetHigh members.) */
typedef struct OVERLAPPED {
    unsigned long long Internal;
    unsigned long long InternalHigh;
    unsigned int Offset;
    unsigned int OffsetHigh;
    void *hEvent;
} OVERLAPPED;

extern void *CreateFileA(const char *name, unsigned int access, unsigned int share,
                         void *sec, unsigned int disposition, unsigned int flags, void *templ);
extern int  ReadFile(void *h, void *buf, unsigned int n, unsigned int *nread, OVERLAPPED *ov);
extern int  WriteFile(void *h, const void *buf, unsigned int n, unsigned int *nwrote, OVERLAPPED *ov);
extern int  SetFilePointerEx(void *h, long long dist, long long *newPos, unsigned int method);
extern int  SetEndOfFile(void *h);
extern int  FlushFileBuffers(void *h);
extern int  CloseHandle(void *h);
extern int  DeleteFileA(const char *name);
extern int  GetFileSizeEx(void *h, long long *size);
extern unsigned int GetFileAttributesA(const char *name);
extern unsigned int GetLastError(void);

/* ---- Win32 file locking + sleep (kernel32) ---- */
#define LOCKFILE_FAIL_IMMEDIATELY 0x00000001u
#define LOCKFILE_EXCLUSIVE_LOCK   0x00000002u
extern int LockFileEx(void *h, unsigned int flags, unsigned int reserved,
                      unsigned int nLow, unsigned int nHigh, OVERLAPPED *ov);
extern int UnlockFileEx(void *h, unsigned int reserved,
                        unsigned int nLow, unsigned int nHigh, OVERLAPPED *ov);
extern void Sleep(unsigned int millis);          /* kernel32 */

/* INVALID_HANDLE_VALUE == (void*)-1 */
#define INVALID_HANDLE_VALUE ((void *)(long long)-1)

#endif
