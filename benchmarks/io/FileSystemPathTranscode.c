#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <stdint.h>
#include <stdio.h>
#include <string.h>

#ifdef _WIN32
#include <direct.h>
#include <windows.h>
#define mkdir_one(path) _mkdir(path)
#define rmdir_one(path) _rmdir(path)
#else
#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>
#define mkdir_one(path) mkdir(path, 0777)
#define rmdir_one(path) rmdir(path)
#endif

static int first_entry_length(const char *path) {
#ifdef _WIN32
    char pattern[256];
    snprintf(pattern, sizeof(pattern), "%s\\*", path);
    WIN32_FIND_DATAA data;
    HANDLE handle = FindFirstFileA(pattern, &data);
    if (handle == INVALID_HANDLE_VALUE) {
        return -1;
    }

    do {
        if (strcmp(data.cFileName, ".") != 0 && strcmp(data.cFileName, "..") != 0) {
            int length = (int)strlen(data.cFileName);
            FindClose(handle);
            return length;
        }
    } while (FindNextFileA(handle, &data));

    FindClose(handle);
    return -1;
#else
    DIR *directory = opendir(path);
    if (directory == NULL) {
        return -1;
    }

    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) {
            int length = (int)strlen(entry->d_name);
            closedir(directory);
            return length;
        }
    }

    closedir(directory);
    return -1;
#endif
}

static int write_utf16_line(FILE *file, const char *text) {
    for (const char *cursor = text; *cursor != '\0'; cursor += 1) {
        uint16_t unit = (uint16_t)(unsigned char)*cursor;
        if (fwrite(&unit, sizeof(unit), 1, file) != 1) {
            return 0;
        }
    }

    uint16_t newline = 10;
    return fwrite(&newline, sizeof(newline), 1, file) == 1;
}

int main(void) {
    static const int iterations = 16;
    int64_t checksum = 0;
    remove("experimental-io-bench-root/renamed.txt");
    remove("experimental-io-bench-root/child.txt");
    rmdir_one("experimental-io-bench-root");

    for (int iteration = 0; iteration < iterations; iteration += 1) {
        if (mkdir_one("experimental-io-bench-root") != 0) {
            return 1;
        }

        const char *child_path = "experimental-io-bench-root/child.txt";
        checksum += (int64_t)strlen(child_path);

        FILE *file = fopen(child_path, "wb");
        if (file == NULL) {
            return 1;
        }

        if (!write_utf16_line(file, "child") ||
            !write_utf16_line(file, "wide") ||
            fclose(file) != 0) {
            return 1;
        }

        checksum += (int64_t)strlen(child_path);
        checksum += first_entry_length("experimental-io-bench-root");

        if (rename(child_path, "experimental-io-bench-root/renamed.txt") != 0 ||
            remove("experimental-io-bench-root/renamed.txt") != 0 ||
            rmdir_one("experimental-io-bench-root") != 0) {
            return 1;
        }
    }

    return checksum == 1296 ? 0 : 1;
}
