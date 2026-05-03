#include <stdint.h>
#include <stddef.h>
#include <string.h>

static int is_separator(char value) {
    return value == '/';
}

static int join_path(char *destination, size_t capacity, const char *left, const char *right, size_t *length) {
    size_t left_length = strlen(left);
    size_t right_length = strlen(right);
    size_t right_start = 0;
    int insert_separator = 0;

    if (left_length > 0 && right_length > 0) {
        if (is_separator(left[left_length - 1])) {
            if (is_separator(right[0])) {
                right_start = 1;
            }
        } else if (!is_separator(right[0])) {
            insert_separator = 1;
        }
    }

    size_t right_copy_length = right_length - right_start;
    size_t total = left_length + right_copy_length + (insert_separator ? 1 : 0);
    if (total + 1 > capacity) {
        return 0;
    }

    size_t write = 0;
    memcpy(destination + write, left, left_length);
    write += left_length;
    if (insert_separator) {
        destination[write] = '/';
        write += 1;
    }

    memcpy(destination + write, right + right_start, right_copy_length);
    write += right_copy_length;
    destination[write] = '\0';
    *length = write;
    return 1;
}

int main(void) {
    char buffer[64] = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 5000; i += 1) {
        size_t length = 0;
        if (!join_path(buffer, sizeof(buffer), "alpha", "beta.txt", &length)) {
            return 1;
        }

        checksum += (int64_t)length;

        if (!join_path(buffer, sizeof(buffer), "alpha/beta", "gamma.txt", &length)) {
            return 2;
        }

        checksum += (int64_t)length;
    }

    return checksum == 170000 ? 0 : 3;
}
