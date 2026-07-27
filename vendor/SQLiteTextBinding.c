#include <sqlite3.h>
#include <stddef.h>
#include <string.h>

#ifndef SQLITE_CARRAY_TEXT
#define SQLITE_CARRAY_TEXT 3
#endif

#ifndef SQLITE_CARRAY_BLOB
#define SQLITE_CARRAY_BLOB 4
#endif

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

#if defined(__GNUC__) && !defined(_WIN32)
extern const char *sqlite3_normalized_sql(sqlite3_stmt *statement) __attribute__((weak));
extern int sqlite3_stmt_scanstatus(sqlite3_stmt *statement, int index, int operation, void *output) __attribute__((weak));
extern int sqlite3_stmt_scanstatus_v2(sqlite3_stmt *statement, int index, int operation, int flags, void *output) __attribute__((weak));
extern void sqlite3_stmt_scanstatus_reset(sqlite3_stmt *statement) __attribute__((weak));
#endif

typedef const char *(*stark_sqlite_normalized_sql_fn)(sqlite3_stmt *statement);
typedef int (*stark_sqlite_stmt_scanstatus_fn)(sqlite3_stmt *statement, int index, int operation, void *output);
typedef int (*stark_sqlite_stmt_scanstatus_v2_fn)(sqlite3_stmt *statement, int index, int operation, int flags, void *output);
typedef void (*stark_sqlite_stmt_scanstatus_reset_fn)(sqlite3_stmt *statement);
typedef int (*stark_sqlite_snapshot_get_fn)(sqlite3 *database, const char *schema, sqlite3_snapshot **snapshot);
typedef int (*stark_sqlite_snapshot_open_fn)(sqlite3 *database, const char *schema, sqlite3_snapshot *snapshot);
typedef void (*stark_sqlite_snapshot_free_fn)(sqlite3_snapshot *snapshot);
typedef int (*stark_sqlite_snapshot_cmp_fn)(sqlite3_snapshot *left, sqlite3_snapshot *right);
typedef int (*stark_sqlite_snapshot_recover_fn)(sqlite3 *database, const char *schema);
typedef int (*stark_sqlite_carray_bind_fn)(
    sqlite3_stmt *statement,
    int index,
    void *data,
    int count,
    int flags,
    sqlite3_destructor_type destroy);
typedef int (*stark_sqlite_carray_bind_v2_fn)(
    sqlite3_stmt *statement,
    int index,
    void *data,
    int count,
    int flags,
    sqlite3_destructor_type destroy,
    void *client_data);
typedef int (*stark_sqlite_mutex_assert_fn)(sqlite3_mutex *mutex);
typedef int (*stark_sqlite_win32_set_directory_fn)(unsigned long type, void *value);
typedef int (*stark_sqlite_win32_set_directory8_fn)(unsigned long type, const char *value);
typedef int (*stark_sqlite_win32_set_directory16_fn)(unsigned long type, const void *value);

typedef struct stark_sqlite_blob_input
{
    const void *data;
    sqlite3_uint64 byte_length;
} stark_sqlite_blob_input;

typedef struct stark_sqlite_iovec
{
    void *iov_base;
    size_t iov_len;
} stark_sqlite_iovec;

static int stark_sqlite_temp_directory_owned = 0;
static int stark_sqlite_data_directory_owned = 0;

static void *stark_sqlite_find_symbol(const char *name)
{
#if defined(_WIN32)
    HMODULE module = GetModuleHandleA("sqlite3.dll");
    if (module == 0)
    {
        module = GetModuleHandleA("sqlite3");
    }

    if (module == 0)
    {
        return 0;
    }

    return (void *)GetProcAddress(module, name);
#else
    return dlsym(RTLD_DEFAULT, name);
#endif
}

static stark_sqlite_normalized_sql_fn stark_sqlite_lookup_normalized_sql(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_NORMALIZE)
    return sqlite3_normalized_sql;
#elif defined(__GNUC__) && !defined(_WIN32)
    if (sqlite3_normalized_sql != 0)
    {
        return sqlite3_normalized_sql;
    }
#endif

    return (stark_sqlite_normalized_sql_fn)stark_sqlite_find_symbol("sqlite3_normalized_sql");
}

static stark_sqlite_stmt_scanstatus_fn stark_sqlite_lookup_stmt_scanstatus(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_STMT_SCANSTATUS)
    return sqlite3_stmt_scanstatus;
#elif defined(__GNUC__) && !defined(_WIN32)
    if (sqlite3_stmt_scanstatus != 0)
    {
        return sqlite3_stmt_scanstatus;
    }
#endif

    return (stark_sqlite_stmt_scanstatus_fn)stark_sqlite_find_symbol("sqlite3_stmt_scanstatus");
}

static stark_sqlite_stmt_scanstatus_v2_fn stark_sqlite_lookup_stmt_scanstatus_v2(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_STMT_SCANSTATUS)
    return sqlite3_stmt_scanstatus_v2;
#elif defined(__GNUC__) && !defined(_WIN32)
    if (sqlite3_stmt_scanstatus_v2 != 0)
    {
        return sqlite3_stmt_scanstatus_v2;
    }
#endif

    return (stark_sqlite_stmt_scanstatus_v2_fn)stark_sqlite_find_symbol("sqlite3_stmt_scanstatus_v2");
}

static stark_sqlite_stmt_scanstatus_reset_fn stark_sqlite_lookup_stmt_scanstatus_reset(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_STMT_SCANSTATUS)
    return sqlite3_stmt_scanstatus_reset;
#elif defined(__GNUC__) && !defined(_WIN32)
    if (sqlite3_stmt_scanstatus_reset != 0)
    {
        return sqlite3_stmt_scanstatus_reset;
    }
#endif

    return (stark_sqlite_stmt_scanstatus_reset_fn)stark_sqlite_find_symbol("sqlite3_stmt_scanstatus_reset");
}

static stark_sqlite_snapshot_get_fn stark_sqlite_lookup_snapshot_get(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_SNAPSHOT)
    return sqlite3_snapshot_get;
#else
    return (stark_sqlite_snapshot_get_fn)stark_sqlite_find_symbol("sqlite3_snapshot_get");
#endif
}

static stark_sqlite_snapshot_open_fn stark_sqlite_lookup_snapshot_open(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_SNAPSHOT)
    return sqlite3_snapshot_open;
#else
    return (stark_sqlite_snapshot_open_fn)stark_sqlite_find_symbol("sqlite3_snapshot_open");
#endif
}

static stark_sqlite_snapshot_free_fn stark_sqlite_lookup_snapshot_free(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_SNAPSHOT)
    return sqlite3_snapshot_free;
#else
    return (stark_sqlite_snapshot_free_fn)stark_sqlite_find_symbol("sqlite3_snapshot_free");
#endif
}

static stark_sqlite_snapshot_cmp_fn stark_sqlite_lookup_snapshot_cmp(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_SNAPSHOT)
    return sqlite3_snapshot_cmp;
#else
    return (stark_sqlite_snapshot_cmp_fn)stark_sqlite_find_symbol("sqlite3_snapshot_cmp");
#endif
}

static stark_sqlite_snapshot_recover_fn stark_sqlite_lookup_snapshot_recover(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_SNAPSHOT)
    return sqlite3_snapshot_recover;
#else
    return (stark_sqlite_snapshot_recover_fn)stark_sqlite_find_symbol("sqlite3_snapshot_recover");
#endif
}

static stark_sqlite_carray_bind_fn stark_sqlite_lookup_carray_bind(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_CARRAY)
    return sqlite3_carray_bind;
#else
    return (stark_sqlite_carray_bind_fn)stark_sqlite_find_symbol("sqlite3_carray_bind");
#endif
}

static stark_sqlite_carray_bind_v2_fn stark_sqlite_lookup_carray_bind_v2(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_CARRAY)
    return sqlite3_carray_bind_v2;
#else
    return (stark_sqlite_carray_bind_v2_fn)stark_sqlite_find_symbol("sqlite3_carray_bind_v2");
#endif
}

static stark_sqlite_mutex_assert_fn stark_sqlite_lookup_mutex_held(void)
{
    return (stark_sqlite_mutex_assert_fn)stark_sqlite_find_symbol("sqlite3_mutex_held");
}

static stark_sqlite_mutex_assert_fn stark_sqlite_lookup_mutex_notheld(void)
{
    return (stark_sqlite_mutex_assert_fn)stark_sqlite_find_symbol("sqlite3_mutex_notheld");
}

static stark_sqlite_win32_set_directory_fn stark_sqlite_lookup_win32_set_directory(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(_WIN32)
    return sqlite3_win32_set_directory;
#else
    return (stark_sqlite_win32_set_directory_fn)stark_sqlite_find_symbol("sqlite3_win32_set_directory");
#endif
}

static stark_sqlite_win32_set_directory8_fn stark_sqlite_lookup_win32_set_directory8(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(_WIN32)
    return sqlite3_win32_set_directory8;
#else
    return (stark_sqlite_win32_set_directory8_fn)stark_sqlite_find_symbol("sqlite3_win32_set_directory8");
#endif
}

static stark_sqlite_win32_set_directory16_fn stark_sqlite_lookup_win32_set_directory16(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(_WIN32)
    return sqlite3_win32_set_directory16;
#else
    return (stark_sqlite_win32_set_directory16_fn)stark_sqlite_find_symbol("sqlite3_win32_set_directory16");
#endif
}

const char *stark_sqlite_version_variable(void)
{
    return sqlite3_version;
}

static char **stark_sqlite_lookup_temp_directory(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES)
    return &sqlite3_temp_directory;
#else
    return (char **)stark_sqlite_find_symbol("sqlite3_temp_directory");
#endif
}

static char **stark_sqlite_lookup_data_directory(void)
{
#if defined(STARK_SQLITE_BUNDLED_FEATURES)
    return &sqlite3_data_directory;
#else
    return (char **)stark_sqlite_find_symbol("sqlite3_data_directory");
#endif
}

static int stark_sqlite_set_directory_variable(char **slot, int *owned, const char *value)
{
    if (slot == 0 || owned == 0)
    {
        return SQLITE_NOTFOUND;
    }

    char *copy = 0;
    if (value != 0)
    {
        copy = sqlite3_mprintf("%s", value);
        if (copy == 0)
        {
            return SQLITE_NOMEM;
        }
    }

    if (*owned && *slot != 0)
    {
        sqlite3_free(*slot);
    }

    *slot = copy;
    *owned = copy != 0;
    return SQLITE_OK;
}

const char *stark_sqlite_temp_directory(void)
{
    char **slot = stark_sqlite_lookup_temp_directory();
    if (slot == 0)
    {
        return 0;
    }

    return *slot;
}

const char *stark_sqlite_data_directory(void)
{
    char **slot = stark_sqlite_lookup_data_directory();
    if (slot == 0)
    {
        return 0;
    }

    return *slot;
}

int stark_sqlite_set_temp_directory(const char *value)
{
    return stark_sqlite_set_directory_variable(
        stark_sqlite_lookup_temp_directory(),
        &stark_sqlite_temp_directory_owned,
        value);
}

int stark_sqlite_set_data_directory(const char *value)
{
    return stark_sqlite_set_directory_variable(
        stark_sqlite_lookup_data_directory(),
        &stark_sqlite_data_directory_owned,
        value);
}

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

int stark_sqlite_carray_bind_available(void)
{
    return stark_sqlite_lookup_carray_bind_v2() != 0 || stark_sqlite_lookup_carray_bind() != 0;
}

int stark_sqlite_carray_bind_v2_available(void)
{
    return stark_sqlite_lookup_carray_bind_v2() != 0;
}

int stark_sqlite_carray_bind_transient(sqlite3_stmt *statement, int index, const void *data, int count, int flags)
{
    if (statement == 0 || index <= 0 || count < 0 || (count != 0 && data == 0))
    {
        return SQLITE_MISUSE;
    }

    stark_sqlite_carray_bind_v2_fn carray_bind_v2 = stark_sqlite_lookup_carray_bind_v2();
    if (carray_bind_v2 != 0)
    {
        return carray_bind_v2(statement, index, (void *)data, count, flags, SQLITE_TRANSIENT, (void *)data);
    }

    stark_sqlite_carray_bind_fn carray_bind = stark_sqlite_lookup_carray_bind();
    if (carray_bind == 0)
    {
        return SQLITE_NOTFOUND;
    }

    return carray_bind(statement, index, (void *)data, count, flags, SQLITE_TRANSIENT);
}

int stark_sqlite_carray_bind_v2_transient(sqlite3_stmt *statement, int index, const void *data, int count, int flags)
{
    if (statement == 0 || index <= 0 || count < 0 || (count != 0 && data == 0))
    {
        return SQLITE_MISUSE;
    }

    stark_sqlite_carray_bind_v2_fn carray_bind_v2 = stark_sqlite_lookup_carray_bind_v2();
    if (carray_bind_v2 == 0)
    {
        return SQLITE_NOTFOUND;
    }

    return carray_bind_v2(statement, index, (void *)data, count, flags, SQLITE_TRANSIENT, (void *)data);
}

static void stark_sqlite_free_carray_payload(void *data)
{
    sqlite3_free(data);
}

static int stark_sqlite_carray_bind_text_copy(sqlite3_stmt *statement, int index, const void *values_data, int count, int require_v2)
{
    stark_sqlite_carray_bind_v2_fn carray_bind_v2;
    stark_sqlite_carray_bind_fn carray_bind;
    const char *const *values;
    sqlite3_uint64 pointer_bytes;
    sqlite3_uint64 text_bytes;
    sqlite3_uint64 total_bytes;
    sqlite3_uint64 max_uint64;
    char **copy;
    char *cursor;
    int i;

    if (statement == 0 || index <= 0 || count < 0 || (count != 0 && values_data == 0))
    {
        return SQLITE_MISUSE;
    }

    values = (const char *const *)values_data;

    carray_bind_v2 = stark_sqlite_lookup_carray_bind_v2();
    carray_bind = 0;
    if (carray_bind_v2 == 0)
    {
        if (require_v2)
        {
            return SQLITE_NOTFOUND;
        }

        carray_bind = stark_sqlite_lookup_carray_bind();
        if (carray_bind == 0)
        {
            return SQLITE_NOTFOUND;
        }
    }

    if (count == 0)
    {
        if (carray_bind_v2 != 0)
        {
            return carray_bind_v2(statement, index, 0, 0, SQLITE_CARRAY_TEXT, SQLITE_TRANSIENT, 0);
        }

        return carray_bind(statement, index, 0, 0, SQLITE_CARRAY_TEXT, SQLITE_TRANSIENT);
    }

    max_uint64 = ~(sqlite3_uint64)0;
    if ((sqlite3_uint64)count > max_uint64 / (sqlite3_uint64)sizeof(char *))
    {
        return SQLITE_TOOBIG;
    }

    pointer_bytes = (sqlite3_uint64)count * (sqlite3_uint64)sizeof(char *);
    text_bytes = 0;
    for (i = 0; i < count; i += 1)
    {
        size_t length;
        sqlite3_uint64 required;

        if (values[i] == 0)
        {
            return SQLITE_MISUSE;
        }

        length = strlen(values[i]);
        if ((sqlite3_uint64)length == max_uint64)
        {
            return SQLITE_TOOBIG;
        }

        required = (sqlite3_uint64)length + 1;
        if (text_bytes > max_uint64 - required)
        {
            return SQLITE_TOOBIG;
        }

        text_bytes += required;
    }

    if (pointer_bytes > max_uint64 - text_bytes)
    {
        return SQLITE_TOOBIG;
    }

    total_bytes = pointer_bytes + text_bytes;
    copy = (char **)sqlite3_malloc64(total_bytes);
    if (copy == 0)
    {
        return SQLITE_NOMEM;
    }

    cursor = (char *)((unsigned char *)copy + pointer_bytes);
    for (i = 0; i < count; i += 1)
    {
        size_t length = strlen(values[i]) + 1;
        memcpy(cursor, values[i], length);
        copy[i] = cursor;
        cursor += length;
    }

    if (carray_bind_v2 != 0)
    {
        return carray_bind_v2(statement, index, copy, count, SQLITE_CARRAY_TEXT, stark_sqlite_free_carray_payload, copy);
    }

    return carray_bind(statement, index, copy, count, SQLITE_CARRAY_TEXT, stark_sqlite_free_carray_payload);
}

int stark_sqlite_carray_bind_text_transient(sqlite3_stmt *statement, int index, const void *values, int count)
{
    return stark_sqlite_carray_bind_text_copy(statement, index, values, count, 0);
}

int stark_sqlite_carray_bind_text_v2_transient(sqlite3_stmt *statement, int index, const void *values, int count)
{
    return stark_sqlite_carray_bind_text_copy(statement, index, values, count, 1);
}

static int stark_sqlite_carray_bind_blob_copy(sqlite3_stmt *statement, int index, const void *values_data, int count, int require_v2)
{
    stark_sqlite_carray_bind_v2_fn carray_bind_v2;
    stark_sqlite_carray_bind_fn carray_bind;
    const stark_sqlite_blob_input *values;
    sqlite3_uint64 iovec_bytes;
    sqlite3_uint64 blob_bytes;
    sqlite3_uint64 total_bytes;
    sqlite3_uint64 max_uint64;
    sqlite3_uint64 max_size_t;
    stark_sqlite_iovec *copy;
    unsigned char *cursor;
    int i;

    if (statement == 0 || index <= 0 || count < 0 || (count != 0 && values_data == 0))
    {
        return SQLITE_MISUSE;
    }

    values = (const stark_sqlite_blob_input *)values_data;

    carray_bind_v2 = stark_sqlite_lookup_carray_bind_v2();
    carray_bind = 0;
    if (carray_bind_v2 == 0)
    {
        if (require_v2)
        {
            return SQLITE_NOTFOUND;
        }

        carray_bind = stark_sqlite_lookup_carray_bind();
        if (carray_bind == 0)
        {
            return SQLITE_NOTFOUND;
        }
    }

    if (count == 0)
    {
        if (carray_bind_v2 != 0)
        {
            return carray_bind_v2(statement, index, 0, 0, SQLITE_CARRAY_BLOB, SQLITE_TRANSIENT, 0);
        }

        return carray_bind(statement, index, 0, 0, SQLITE_CARRAY_BLOB, SQLITE_TRANSIENT);
    }

    max_uint64 = ~(sqlite3_uint64)0;
    max_size_t = (sqlite3_uint64)(~(size_t)0);
    if ((sqlite3_uint64)count > max_uint64 / (sqlite3_uint64)sizeof(stark_sqlite_iovec))
    {
        return SQLITE_TOOBIG;
    }

    iovec_bytes = (sqlite3_uint64)count * (sqlite3_uint64)sizeof(stark_sqlite_iovec);
    blob_bytes = 0;
    for (i = 0; i < count; i += 1)
    {
        if (values[i].byte_length > max_size_t)
        {
            return SQLITE_TOOBIG;
        }

        if (values[i].byte_length != 0 && values[i].data == 0)
        {
            return SQLITE_MISUSE;
        }

        if (blob_bytes > max_uint64 - values[i].byte_length)
        {
            return SQLITE_TOOBIG;
        }

        blob_bytes += values[i].byte_length;
    }

    if (iovec_bytes > max_uint64 - blob_bytes)
    {
        return SQLITE_TOOBIG;
    }

    total_bytes = iovec_bytes + blob_bytes;
    copy = (stark_sqlite_iovec *)sqlite3_malloc64(total_bytes);
    if (copy == 0)
    {
        return SQLITE_NOMEM;
    }

    cursor = (unsigned char *)copy + iovec_bytes;
    for (i = 0; i < count; i += 1)
    {
        if (values[i].byte_length == 0)
        {
            copy[i].iov_base = 0;
            copy[i].iov_len = 0;
        }
        else
        {
            size_t byte_length = (size_t)values[i].byte_length;
            memcpy(cursor, values[i].data, byte_length);
            copy[i].iov_base = cursor;
            copy[i].iov_len = byte_length;
            cursor += byte_length;
        }
    }

    if (carray_bind_v2 != 0)
    {
        return carray_bind_v2(statement, index, copy, count, SQLITE_CARRAY_BLOB, stark_sqlite_free_carray_payload, copy);
    }

    return carray_bind(statement, index, copy, count, SQLITE_CARRAY_BLOB, stark_sqlite_free_carray_payload);
}

int stark_sqlite_carray_bind_blob_transient(sqlite3_stmt *statement, int index, const void *values, int count)
{
    return stark_sqlite_carray_bind_blob_copy(statement, index, values, count, 0);
}

int stark_sqlite_carray_bind_blob_v2_transient(sqlite3_stmt *statement, int index, const void *values, int count)
{
    return stark_sqlite_carray_bind_blob_copy(statement, index, values, count, 1);
}

int stark_sqlite_mutex_held_available(void)
{
    return stark_sqlite_lookup_mutex_held() != 0;
}

int stark_sqlite_mutex_notheld_available(void)
{
    return stark_sqlite_lookup_mutex_notheld() != 0;
}

int stark_sqlite_mutex_held(sqlite3_mutex *mutex, int *output)
{
    stark_sqlite_mutex_assert_fn mutex_held = stark_sqlite_lookup_mutex_held();
    if (mutex_held == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (output == 0)
    {
        return SQLITE_MISUSE;
    }

    *output = mutex_held(mutex) != 0;
    return SQLITE_OK;
}

int stark_sqlite_mutex_notheld(sqlite3_mutex *mutex, int *output)
{
    stark_sqlite_mutex_assert_fn mutex_notheld = stark_sqlite_lookup_mutex_notheld();
    if (mutex_notheld == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (output == 0)
    {
        return SQLITE_MISUSE;
    }

    *output = mutex_notheld(mutex) != 0;
    return SQLITE_OK;
}

int stark_sqlite_win32_set_directory_available(void)
{
    return stark_sqlite_lookup_win32_set_directory() != 0;
}

int stark_sqlite_win32_set_directory8_available(void)
{
    return stark_sqlite_lookup_win32_set_directory8() != 0;
}

int stark_sqlite_win32_set_directory16_available(void)
{
    return stark_sqlite_lookup_win32_set_directory16() != 0;
}

int stark_sqlite_win32_set_directory(unsigned long type, const void *value)
{
    stark_sqlite_win32_set_directory_fn set_directory = stark_sqlite_lookup_win32_set_directory();
    if (set_directory == 0)
    {
        return SQLITE_NOTFOUND;
    }

    return set_directory(type, (void *)value);
}

int stark_sqlite_win32_set_directory8(unsigned long type, const char *value)
{
    stark_sqlite_win32_set_directory8_fn set_directory = stark_sqlite_lookup_win32_set_directory8();
    if (set_directory == 0)
    {
        return SQLITE_NOTFOUND;
    }

    return set_directory(type, value);
}

int stark_sqlite_win32_set_directory16(unsigned long type, const void *value)
{
    stark_sqlite_win32_set_directory16_fn set_directory = stark_sqlite_lookup_win32_set_directory16();
    if (set_directory == 0)
    {
        return SQLITE_NOTFOUND;
    }

    return set_directory(type, value);
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

const char *stark_sqlite_table_entry(void *table, sqlite3_uint64 index)
{
    if (table == 0)
    {
        return 0;
    }

    return ((char **)table)[index];
}

int stark_sqlite_normalized_sql_available(void)
{
    return stark_sqlite_lookup_normalized_sql() != 0;
}

int stark_sqlite_stmt_scanstatus_available(void)
{
    return stark_sqlite_lookup_stmt_scanstatus_v2() != 0 || stark_sqlite_lookup_stmt_scanstatus() != 0;
}

int stark_sqlite_stmt_scanstatus_v2_available(void)
{
    return stark_sqlite_lookup_stmt_scanstatus_v2() != 0;
}

int stark_sqlite_normalized_sql(sqlite3_stmt *statement, const char **output)
{
    stark_sqlite_normalized_sql_fn normalized_sql = stark_sqlite_lookup_normalized_sql();
    if (normalized_sql == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (statement == 0 || output == 0)
    {
        return SQLITE_MISUSE;
    }

    *output = normalized_sql(statement);
    return SQLITE_OK;
}

static int stark_sqlite_stmt_scanstatus_any(sqlite3_stmt *statement, int index, int operation, int flags, void *output)
{
    stark_sqlite_stmt_scanstatus_v2_fn scanstatus_v2 = stark_sqlite_lookup_stmt_scanstatus_v2();
    if (scanstatus_v2 != 0)
    {
        return scanstatus_v2(statement, index, operation, flags, output);
    }

    if (flags != 0)
    {
        return SQLITE_NOTFOUND;
    }

    stark_sqlite_stmt_scanstatus_fn scanstatus = stark_sqlite_lookup_stmt_scanstatus();
    if (scanstatus == 0)
    {
        return SQLITE_NOTFOUND;
    }

    return scanstatus(statement, index, operation, output);
}

int stark_sqlite_stmt_scanstatus_i64(sqlite3_stmt *statement, int index, int operation, int flags, sqlite3_int64 *output)
{
    if (statement == 0 || output == 0)
    {
        return SQLITE_MISUSE;
    }

    return stark_sqlite_stmt_scanstatus_any(statement, index, operation, flags, output);
}

int stark_sqlite_stmt_scanstatus_int(sqlite3_stmt *statement, int index, int operation, int flags, int *output)
{
    if (statement == 0 || output == 0)
    {
        return SQLITE_MISUSE;
    }

    return stark_sqlite_stmt_scanstatus_any(statement, index, operation, flags, output);
}

int stark_sqlite_stmt_scanstatus_double(sqlite3_stmt *statement, int index, int operation, int flags, double *output)
{
    if (statement == 0 || output == 0)
    {
        return SQLITE_MISUSE;
    }

    return stark_sqlite_stmt_scanstatus_any(statement, index, operation, flags, output);
}

int stark_sqlite_stmt_scanstatus_text(sqlite3_stmt *statement, int index, int operation, int flags, const char **output)
{
    if (statement == 0 || output == 0)
    {
        return SQLITE_MISUSE;
    }

    return stark_sqlite_stmt_scanstatus_any(statement, index, operation, flags, output);
}

int stark_sqlite_stmt_scanstatus_reset(sqlite3_stmt *statement)
{
    stark_sqlite_stmt_scanstatus_reset_fn reset = stark_sqlite_lookup_stmt_scanstatus_reset();
    if (reset == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (statement == 0)
    {
        return SQLITE_MISUSE;
    }

    reset(statement);
    return SQLITE_OK;
}

int stark_sqlite_snapshot_available(void)
{
    return stark_sqlite_lookup_snapshot_get() != 0
        && stark_sqlite_lookup_snapshot_open() != 0
        && stark_sqlite_lookup_snapshot_free() != 0
        && stark_sqlite_lookup_snapshot_cmp() != 0
        && stark_sqlite_lookup_snapshot_recover() != 0;
}

int stark_sqlite_snapshot_get(sqlite3 *database, const char *schema, sqlite3_snapshot **snapshot)
{
    stark_sqlite_snapshot_get_fn snapshot_get = stark_sqlite_lookup_snapshot_get();
    if (snapshot_get == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (database == 0 || schema == 0 || snapshot == 0)
    {
        return SQLITE_MISUSE;
    }

    *snapshot = 0;
    return snapshot_get(database, schema, snapshot);
}

int stark_sqlite_snapshot_open(sqlite3 *database, const char *schema, sqlite3_snapshot *snapshot)
{
    stark_sqlite_snapshot_open_fn snapshot_open = stark_sqlite_lookup_snapshot_open();
    if (snapshot_open == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (database == 0 || schema == 0 || snapshot == 0)
    {
        return SQLITE_MISUSE;
    }

    return snapshot_open(database, schema, snapshot);
}

void stark_sqlite_snapshot_free(sqlite3_snapshot *snapshot)
{
    stark_sqlite_snapshot_free_fn snapshot_free = stark_sqlite_lookup_snapshot_free();
    if (snapshot != 0 && snapshot_free != 0)
    {
        snapshot_free(snapshot);
    }
}

int stark_sqlite_snapshot_cmp(sqlite3_snapshot *left, sqlite3_snapshot *right, int *output)
{
    stark_sqlite_snapshot_cmp_fn snapshot_cmp = stark_sqlite_lookup_snapshot_cmp();
    if (snapshot_cmp == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (left == 0 || right == 0 || output == 0)
    {
        return SQLITE_MISUSE;
    }

    *output = snapshot_cmp(left, right);
    return SQLITE_OK;
}

int stark_sqlite_snapshot_recover(sqlite3 *database, const char *schema)
{
    stark_sqlite_snapshot_recover_fn snapshot_recover = stark_sqlite_lookup_snapshot_recover();
    if (snapshot_recover == 0)
    {
        return SQLITE_NOTFOUND;
    }

    if (database == 0 || schema == 0)
    {
        return SQLITE_MISUSE;
    }

    return snapshot_recover(database, schema);
}
