#include <stdint.h>
#include <stddef.h>
#include <string.h>

static int concat_ascii(char *destination, size_t capacity, const char *left, const char *right, size_t *length) {
    size_t left_length = strlen(left);
    size_t right_length = strlen(right);
    size_t total = left_length + right_length;
    if (total + 1 > capacity) {
        return 0;
    }

    memcpy(destination, left, left_length);
    memcpy(destination + left_length, right, right_length);
    destination[total] = '\0';
    *length = total;
    return 1;
}

static int concat_unicode(int32_t *destination, size_t capacity, const char *left, const char *right, size_t *length) {
    size_t left_length = strlen(left);
    size_t right_length = strlen(right);
    size_t total = left_length + right_length;
    if (total > capacity) {
        return 0;
    }

    for (size_t i = 0; i < left_length; i += 1) {
        destination[i] = (int32_t)left[i];
    }

    for (size_t i = 0; i < right_length; i += 1) {
        destination[left_length + i] = (int32_t)right[i];
    }

    *length = total;
    return 1;
}

static int64_t checksum_ascii(const char *text, size_t length) {
    return (int64_t)length + (int64_t)text[0] + (int64_t)text[length - 1];
}

static int64_t checksum_unicode(const int32_t *text, size_t length) {
    return (int64_t)length + (int64_t)text[0] + (int64_t)text[length - 1];
}

int main(void) {
    char ascii[32] = {0};
    int32_t unicode[32] = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 5000; i += 1) {
        size_t ascii_length = 0;
        if (!concat_ascii(ascii, sizeof(ascii), "prefix/", "body", &ascii_length)) {
            return 1;
        }

        checksum += checksum_ascii(ascii, ascii_length);

        size_t unicode_length = 0;
        if (!concat_unicode(unicode, sizeof(unicode) / sizeof(unicode[0]), "Value:", "body", &unicode_length)) {
            return 2;
        }

        checksum += checksum_unicode(unicode, unicode_length);
    }

    return checksum == 2305000 ? 0 : 3;
}
