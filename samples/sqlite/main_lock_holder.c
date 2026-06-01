/* main_lock_holder.c — acquires a write lock (BEGIN IMMEDIATE), signals via
   held.marker, waits for release.marker, then commits. */
#include "sqlite3.h"
#include "chibil_os.h"
void platform_init(void);
void register_disk_vfs(void);

static void touch(const char *p){
    if (__chibil_os_is_windows()){
        void *h = CreateFileA(p, GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
        if ((void*)h != INVALID_HANDLE_VALUE) CloseHandle(h);
    } else { int fd = open(p, O_RDWR|O_CREAT, 420); if (fd >= 0) close(fd); }
}
static int exists(const char *p){
    if (__chibil_os_is_windows()) return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
    return access(p, F_OK) == 0;
}
static void sleep_ms(unsigned int ms){ if (__chibil_os_is_windows()) Sleep(ms); else usleep(ms*1000u); }

int main(void){
    platform_init(); register_disk_vfs();
    sqlite3 *db = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 101;
    if (sqlite3_exec(db, "CREATE TABLE IF NOT EXISTS t(a INTEGER);", 0,0,0) != SQLITE_OK) return 102;
    if (sqlite3_exec(db, "BEGIN IMMEDIATE; INSERT INTO t VALUES(1);", 0,0,0) != SQLITE_OK) return 103; /* holds RESERVED */
    touch("held.marker");
    for (int i = 0; i < 3000 && !exists("release.marker"); i++) sleep_ms(10);   /* up to ~30s */
    sqlite3_exec(db, "COMMIT;", 0,0,0);
    sqlite3_close(db);
    return 0;
}
