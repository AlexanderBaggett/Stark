// stark-bench: skip-c-windows
#include <stdint.h>
#include <string.h>
#include <unistd.h>

enum {
    ELEMENT_COUNT = 32,
    SMALL_COUNT = 4,
    ITERATIONS = 200000,
    CHECKSUM_MODULUS = 1000000007
};

static void copy_i64(
    const int64_t input[static ELEMENT_COUNT],
    int64_t output[static ELEMENT_COUNT]) {
    memcpy(output, input, sizeof(int64_t) * ELEMENT_COUNT);
}

static void copy_with_scalar_work(
    const int64_t input[static ELEMENT_COUNT],
    int64_t output[static ELEMENT_COUNT]) {
    memcpy(output, input, sizeof(int64_t) * ELEMENT_COUNT);
    output[0] += ELEMENT_COUNT;
}

static void overlap_safe_copy_with_scalar_work(
    const int64_t *input,
    int64_t *output) {
    memmove(output, input, sizeof(int64_t) * SMALL_COUNT);
    output[0] += 1;
}

static void fill_i64(
    int64_t output[static ELEMENT_COUNT],
    int64_t value) {
    for (int32_t index = 0; index < ELEMENT_COUNT; index += 1) {
        output[index] = value + index;
    }
}

static void fill_bytes_with_scalar_work(
    int8_t output[static ELEMENT_COUNT],
    int8_t value) {
    memset(output, value, ELEMENT_COUNT);
    output[0] = ELEMENT_COUNT;
}

static void transform_i64(
    const int64_t input[static ELEMENT_COUNT],
    int64_t output[static ELEMENT_COUNT]) {
    for (int32_t index = 0; index < ELEMENT_COUNT; index += 1) {
        output[index] = input[index] + 1;
    }
}

int main(void) {
    int64_t input[ELEMENT_COUNT] = {
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32
    };
    int64_t scratch[ELEMENT_COUNT] = {0};
    int64_t output[ELEMENT_COUNT] = {0};
    int8_t bytes[ELEMENT_COUNT] = {0};
    int64_t checksum = (int64_t)getpid();

    for (int32_t iteration = 0; iteration < ITERATIONS; iteration += 1) {
        input[0] += 1;
        fill_i64(scratch, iteration);
        copy_i64(input, output);
        copy_with_scalar_work(input, scratch);
        overlap_safe_copy_with_scalar_work(scratch, output);
        fill_bytes_with_scalar_work(bytes, (int8_t)(iteration % 127));
        transform_i64(scratch, output);
        checksum += output[0] + output[31] + bytes[0];
        checksum %= CHECKSUM_MODULUS;
    }

    return checksum <= 0 ? 1 : 0;
}
