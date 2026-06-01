/* main_lock_contender.c — tries to write; 55 if blocked (SQLITE_BUSY), 0 if it
   acquires (and commits), 44 otherwise. busy_timeout=0 → fail immediately. */
#include "sqlite3.h"
void platform_init(void);
void register_disk_vfs(void);

int main(void){
    platform_init(); register_disk_vfs();
    sqlite3 *db = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 101;
    sqlite3_busy_timeout(db, 0);
    int rc = sqlite3_exec(db, "BEGIN IMMEDIATE; INSERT INTO t VALUES(2);", 0,0,0);
    if (rc == SQLITE_BUSY){ sqlite3_close(db); return 55; }                 /* blocked by holder */
    if (rc == SQLITE_OK){ sqlite3_exec(db, "COMMIT;", 0,0,0); sqlite3_close(db); return 0; } /* acquired */
    sqlite3_close(db); return 44;                                          /* unexpected */
}
