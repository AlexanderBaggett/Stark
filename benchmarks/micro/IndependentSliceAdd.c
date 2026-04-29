// stark-bench: skip-c-windows
#include <stdint.h>
#include <unistd.h>

enum {
    ELEMENT_COUNT = 32,
    ITERATIONS = 200000,
    CHECKSUM_MODULUS = 1000000007
};

static void add_i64(
    const int64_t left[static ELEMENT_COUNT],
    const int64_t right[static ELEMENT_COUNT],
    int64_t output[static ELEMENT_COUNT]) {
    for (int32_t index = 0; index < ELEMENT_COUNT; index += 1) {
        output[index] = left[index] + right[index];
    }
}

int main(void) {
    int64_t left[ELEMENT_COUNT] = {
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32
    };
    int64_t right[ELEMENT_COUNT] = {
        32, 31, 30, 29, 28, 27, 26, 25,
        24, 23, 22, 21, 20, 19, 18, 17,
        16, 15, 14, 13, 12, 11, 10, 9,
        8, 7, 6, 5, 4, 3, 2, 1
    };
    int64_t output[ELEMENT_COUNT] = {0};
    int64_t checksum = (int64_t)getpid();

    for (int32_t iteration = 0; iteration < ITERATIONS; iteration += 1) {
        left[0] += 1;
        right[31] += 1;
        add_i64(left, right, output);
        checksum += output[0] + output[31];
        checksum %= CHECKSUM_MODULUS;
    }

    return checksum <= 0 ? 1 : 0;
}
