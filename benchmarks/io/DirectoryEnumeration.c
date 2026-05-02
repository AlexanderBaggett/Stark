#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#include <direct.h>
#include <windows.h>
#include <wchar.h>
#ifndef FIND_FIRST_EX_LARGE_FETCH
#define FIND_FIRST_EX_LARGE_FETCH 2
#endif
#define mkdir_one(path) _mkdir(path)
#define rmdir_one(path) _rmdir(path)
#else
#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>
#define mkdir_one(path) mkdir(path, 0777)
#define rmdir_one(path) rmdir(path)
#endif

#include <stdint.h>
#include <stdio.h>
#include <string.h>

static const int entries = 128;
static const int ascii_entries = 120;
static const int unicode_entries = 8;
static const int iterations = 128;
static const int64_t expected_checksum = 212992;
static const char *root_name = "dir-enum-bench";

static void ascii_entry_path(char *buffer, size_t capacity, int index) {
    snprintf(buffer, capacity, "%s/entry-%03d.tmp", root_name, index);
}

#ifdef _WIN32
static void unicode_entry_path_w(wchar_t *buffer, size_t capacity, int index) {
    _snwprintf(buffer, capacity, L"dir-enum-bench\\wide-\x00E9-%d.tmp", index);
    buffer[capacity - 1] = 0;
}

static int create_file_w(const wchar_t *path) {
    FILE *file = _wfopen(path, L"wb");
    if (file == NULL) {
        return 0;
    }

    return fclose(file) == 0;
}
#else
static void unicode_entry_path(char *buffer, size_t capacity, int index) {
    snprintf(buffer, capacity, "%s/wide-\xC3\xA9-%d.tmp", root_name, index);
}
#endif

static void delete_entries(void) {
    char path[64];
    for (int index = 0; index < ascii_entries; index += 1) {
        ascii_entry_path(path, sizeof(path), index);
        remove(path);
    }

#ifdef _WIN32
    wchar_t wide_path[64];
    for (int index = 0; index < unicode_entries; index += 1) {
        unicode_entry_path_w(wide_path, sizeof(wide_path) / sizeof(wide_path[0]), index);
        _wremove(wide_path);
    }
#else
    for (int index = 0; index < unicode_entries; index += 1) {
        unicode_entry_path(path, sizeof(path), index);
        remove(path);
    }
#endif

    rmdir_one(root_name);
}

static int create_entries(void) {
    char path[64];

    delete_entries();
    if (mkdir_one(root_name) != 0) {
        return 0;
    }

    for (int index = 0; index < ascii_entries; index += 1) {
        ascii_entry_path(path, sizeof(path), index);
        FILE *file = fopen(path, "wb");
        if (file == NULL) {
            return 0;
        }

        if (fclose(file) != 0) {
            return 0;
        }
    }

#ifdef _WIN32
    wchar_t wide_path[64];
    for (int index = 0; index < unicode_entries; index += 1) {
        unicode_entry_path_w(wide_path, sizeof(wide_path) / sizeof(wide_path[0]), index);
        if (!create_file_w(wide_path)) {
            return 0;
        }
    }
#else
    for (int index = 0; index < unicode_entries; index += 1) {
        unicode_entry_path(path, sizeof(path), index);
        FILE *file = fopen(path, "wb");
        if (file == NULL) {
            return 0;
        }

        if (fclose(file) != 0) {
            return 0;
        }
    }
#endif

    return 1;
}

static int64_t enumerate_once(void) {
    int64_t checksum = 0;
    int count = 0;

#ifdef _WIN32
    WIN32_FIND_DATAW data;
    HANDLE handle = FindFirstFileExW(
        L"dir-enum-bench\\*",
        FindExInfoBasic,
        &data,
        FindExSearchNameMatch,
        NULL,
        FIND_FIRST_EX_LARGE_FETCH);
    if (handle == INVALID_HANDLE_VALUE) {
        return -1;
    }

    do {
        if (wcscmp(data.cFileName, L".") == 0 || wcscmp(data.cFileName, L"..") == 0) {
            continue;
        }

        int utf8_length = WideCharToMultiByte(CP_UTF8, 0, data.cFileName, -1, NULL, 0, NULL, NULL);
        if (utf8_length <= 1) {
            FindClose(handle);
            return -1;
        }

        checksum += (int64_t)(utf8_length - 1);
        count += 1;
    } while (FindNextFileW(handle, &data));

    FindClose(handle);
#else
    DIR *directory = opendir(root_name);
    if (directory == NULL) {
        return -1;
    }

    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }

        checksum += (int64_t)strlen(entry->d_name);
        count += 1;
    }

    closedir(directory);
#endif

    return count == entries ? checksum : -1;
}

int main(void) {
    if (!create_entries()) {
        delete_entries();
        return 1;
    }

    int64_t checksum = 0;
    for (int iteration = 0; iteration < iterations; iteration += 1) {
        int64_t iteration_checksum = enumerate_once();
        if (iteration_checksum < 0) {
            delete_entries();
            return 2;
        }

        checksum += iteration_checksum;
    }

    delete_entries();
    return checksum == expected_checksum ? 0 : 3;
}
