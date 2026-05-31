#include "sqlite3.h"
void platform_init(void);
int main(void){
    platform_init();
    sqlite3 *db = 0;
    if (sqlite3_open(":memory:", &db) != SQLITE_OK) return 101;
    if (sqlite3_exec(db, "CREATE TABLE t(a INTEGER);"
                         "INSERT INTO t VALUES(20),(22),(13);", 0,0,0) != SQLITE_OK) return 102;
    sqlite3_stmt *st = 0;
    if (sqlite3_prepare_v2(db, "SELECT sum(a) FROM t", -1, &st, 0) != SQLITE_OK) return 103;
    int sum = 0;
    if (sqlite3_step(st) == SQLITE_ROW) sum = sqlite3_column_int(st, 0);
    sqlite3_finalize(st);
    sqlite3_close(db);
    return sum;   /* expect 55 */
}
