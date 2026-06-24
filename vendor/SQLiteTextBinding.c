#include <sqlite3.h>

int stark_sqlite_bind_text_transient(sqlite3_stmt *statement, int index, const char *text, int byte_count)
{
    return sqlite3_bind_text(statement, index, text, byte_count, SQLITE_TRANSIENT);
}

int stark_sqlite_bind_text16_transient(sqlite3_stmt *statement, int index, const void *text, int byte_count)
{
    return sqlite3_bind_text16(statement, index, text, byte_count, SQLITE_TRANSIENT);
}

int stark_sqlite_bind_text64_transient(sqlite3_stmt *statement, int index, const char *text, sqlite3_uint64 byte_count, unsigned int encoding)
{
    return sqlite3_bind_text64(statement, index, text, byte_count, SQLITE_TRANSIENT, (unsigned char)encoding);
}

int stark_sqlite_bind_blob_transient(sqlite3_stmt *statement, int index, const void *data, int byte_count)
{
    return sqlite3_bind_blob(statement, index, data, byte_count, SQLITE_TRANSIENT);
}

int stark_sqlite_bind_blob64_transient(sqlite3_stmt *statement, int index, const void *data, sqlite3_uint64 byte_count)
{
    return sqlite3_bind_blob64(statement, index, data, byte_count, SQLITE_TRANSIENT);
}

void stark_sqlite_result_text_transient(sqlite3_context *context, const char *text, int byte_count)
{
    sqlite3_result_text(context, text, byte_count, SQLITE_TRANSIENT);
}

void stark_sqlite_result_text16_transient(sqlite3_context *context, const void *text, int byte_count)
{
    sqlite3_result_text16(context, text, byte_count, SQLITE_TRANSIENT);
}

void stark_sqlite_result_text16le_transient(sqlite3_context *context, const void *text, int byte_count)
{
    sqlite3_result_text16le(context, text, byte_count, SQLITE_TRANSIENT);
}

void stark_sqlite_result_text16be_transient(sqlite3_context *context, const void *text, int byte_count)
{
    sqlite3_result_text16be(context, text, byte_count, SQLITE_TRANSIENT);
}

void stark_sqlite_result_text64_transient(sqlite3_context *context, const char *text, sqlite3_uint64 byte_count, unsigned int encoding)
{
    sqlite3_result_text64(context, text, byte_count, SQLITE_TRANSIENT, (unsigned char)encoding);
}

void stark_sqlite_result_blob_transient(sqlite3_context *context, const void *data, int byte_count)
{
    sqlite3_result_blob(context, data, byte_count, SQLITE_TRANSIENT);
}

void stark_sqlite_result_blob64_transient(sqlite3_context *context, const void *data, sqlite3_uint64 byte_count)
{
    sqlite3_result_blob64(context, data, byte_count, SQLITE_TRANSIENT);
}

sqlite3_value *stark_sqlite_function_argument(sqlite3_value **values, int index)
{
    if (values == 0 || index < 0)
    {
        return 0;
    }

    return values[index];
}
