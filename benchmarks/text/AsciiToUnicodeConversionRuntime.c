#include <stdint.h>
#include <stddef.h>
#include <string.h>

#define ITERATIONS 50000
#define CAPACITY 512

int main(int argc, char **argv) {
    if (argc <= 0 || argv == NULL || argv[0] == NULL) {
        return 1;
    }

    const unsigned char *source = (const unsigned char *)argv[0];
    size_t source_length = strlen(argv[0]);
    if (source_length == 0 || source_length > CAPACITY) {
        return 1;
    }

    int32_t unicode[CAPACITY] = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < ITERATIONS; i += 1) {
        for (size_t index = 0; index < source_length; index += 1) {
            unsigned char unit = source[index];
            if ((unit & 0x80u) != 0) {
                return 2;
            }

            unicode[index] = (int32_t)unit;
        }

        checksum += (int64_t)source_length;
        for (size_t index = 0; index < source_length; index += 1) {
            checksum += (int64_t)unicode[index];
        }
    }

    return checksum > 0 ? 0 : 3;
}
