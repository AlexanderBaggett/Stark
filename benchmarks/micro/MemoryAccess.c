#include <stdint.h>
#include <unistd.h>

int main(void) {
    int64_t seed = (int64_t)(getpid() % 31);
    int64_t values[16] = {
        1, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
        13, 14, 15, 16
    };
    int64_t checksum = 0;

    for (int32_t i = 0; i < 200000; i += 1) {
        int32_t index = i % 16;
        values[index] = values[index] + (int64_t)index + seed;
        checksum += values[index];
    }

    return checksum <= 0 ? 1 : 0;
}
