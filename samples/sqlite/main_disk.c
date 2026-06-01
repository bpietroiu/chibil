/* samples/sqlite/main_disk.c — on-disk CRUD: write, close, REOPEN, read back.
 * Returns SELECT sum(a) = 55, proving the data round-tripped through sp3.db. */
#include "sqlite3.h"
void platform_init(void);
void register_disk_vfs(void);

static int run(void){
    sqlite3 *db = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 101;
    if (sqlite3_exec(db, "CREATE TABLE IF NOT EXISTS t(a INTEGER);"
                         "DELETE FROM t;"
                         "INSERT INTO t VALUES(20),(22),(13);", 0,0,0) != SQLITE_OK) { sqlite3_close(db); return 102; }
    sqlite3_close(db);                       /* flush + close the file */
    return 0;
}
static int readback(void){
    sqlite3 *db = 0; int sum = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 103;   /* fresh connection */
    sqlite3_stmt *st = 0;
    if (sqlite3_prepare_v2(db, "SELECT sum(a) FROM t", -1, &st, 0) != SQLITE_OK) return 104;
    if (sqlite3_step(st) == SQLITE_ROW) sum = sqlite3_column_int(st, 0);
    sqlite3_finalize(st);
    sqlite3_close(db);
    return sum;
}
int main(void){
    platform_init();
    register_disk_vfs();
    int rc = run(); if (rc) return rc;
    return readback();   /* expect 55 */
}
