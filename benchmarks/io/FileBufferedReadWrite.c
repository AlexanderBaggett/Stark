#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <stdint.h>
#include <stdio.h>

static int64_t sum_bytes(const int8_t *values, int64_t count) {
    int64_t checksum = 0;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

int main(void) {
    static const int iterations = 64;
    static const int chunks = 33;
    const int8_t source[32] = {
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16
    };
    int8_t destination[32] = {0};
    int64_t checksum = 0;

    remove("experimental-buffered-rw.tmp");

    for (int iteration = 0; iteration < iterations; iteration += 1) {
        FILE *writer = fopen("experimental-buffered-rw.tmp", "wb");
        if (writer == NULL) {
            return 1;
        }

        for (int chunk = 0; chunk < 32; chunk += 1) {
            if (fwrite(source, 1, sizeof(source), writer) != sizeof(source)) {
                fclose(writer);
                return 1;
            }
        }

        if (fwrite(source, 1, sizeof(source), writer) != sizeof(source) ||
            fclose(writer) != 0) {
            return 1;
        }

        FILE *reader = fopen("experimental-buffered-rw.tmp", "rb");
        if (reader == NULL) {
            return 1;
        }

        if (fseek(reader, 0, SEEK_SET) != 0) {
            fclose(reader);
            return 1;
        }

        for (int chunk = 0; chunk < chunks; chunk += 1) {
            if (fread(destination, 1, sizeof(destination), reader) != sizeof(destination)) {
                fclose(reader);
                return 1;
            }

            checksum += sum_bytes(destination, 32);
        }

        fclose(reader);
        if (remove("experimental-buffered-rw.tmp") != 0) {
            return 1;
        }
    }

    return checksum == 574464 ? 0 : 1;
}
