#include <sqlite3.h>

int stark_sqlite_bind_text_transient(sqlite3_stmt *statement, int index, const char *text, int byte_count)
{
    return sqlite3_bind_text(statement, index, text, byte_count, SQLITE_TRANSIENT);
}
