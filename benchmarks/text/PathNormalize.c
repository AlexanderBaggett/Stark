#include <stdint.h>
#include <stddef.h>

static int normalize_separators(char *destination, size_t capacity, const char *source, size_t *length) {
    size_t write = 0;
    int previous_separator = 0;

    for (size_t read = 0; source[read] != '\0'; read += 1) {
        char value = source[read] == '\\' ? '/' : source[read];
        int separator = value == '/';
        if (separator && previous_separator) {
            continue;
        }

        if (write + 1 >= capacity) {
            return 0;
        }

        destination[write] = value;
        write += 1;
        previous_separator = separator;
    }

    destination[write] = '\0';
    *length = write;
    return 1;
}

int main(void) {
    char buffer[64] = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 5000; i += 1) {
        size_t length = 0;
        if (!normalize_separators(buffer, sizeof(buffer), "alpha//beta///gamma.txt", &length)) {
            return 2;
        }

        checksum += (int64_t)length;

        if (!normalize_separators(buffer, sizeof(buffer), "alpha///beta.txt", &length)) {
            return 3;
        }

        checksum += (int64_t)length;
    }

    return checksum == 170000 ? 0 : 4;
}
