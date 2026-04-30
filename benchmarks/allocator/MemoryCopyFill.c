#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static int64_t sum_bytes(const int8_t *values, int64_t count) {
    int64_t checksum = 0;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

static int64_t sum_code_points(const int32_t *values, int64_t count) {
    int64_t checksum = 0;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

int main(void) {
    static const int iterations = 10000;
    static const int64_t byte_count = 32;
    static const int64_t code_point_count = 32;
    static const int64_t move_count = 16;
    static const int64_t move_destination_start = 8;
    int8_t *byte_source = (int8_t *)malloc((size_t)byte_count);
    int8_t *byte_destination = (int8_t *)malloc((size_t)byte_count);
    int32_t *code_point_source = (int32_t *)malloc((size_t)code_point_count * sizeof(int32_t));
    int32_t *code_point_destination = (int32_t *)malloc((size_t)code_point_count * sizeof(int32_t));
    int8_t byte_move_buffer[32];
    int32_t code_point_move_buffer[32];

    if (byte_source == NULL || byte_destination == NULL ||
        code_point_source == NULL || code_point_destination == NULL) {
        free(byte_source);
        free(byte_destination);
        free(code_point_source);
        free(code_point_destination);
        return 1;
    }

    memset(byte_source, 3, (size_t)byte_count);
    memset(byte_destination, 0, (size_t)byte_count);
    for (int64_t index = 0; index < code_point_count; index += 1) {
        code_point_source[index] = 65;
        code_point_destination[index] = 0;
    }

    int64_t checksum = 0;
    for (int iteration = 0; iteration < iterations; iteration += 1) {
        memcpy(byte_destination, byte_source, (size_t)byte_count);
        checksum += sum_bytes(byte_destination, byte_count);

        memset(byte_destination, (int8_t)((iteration % 17) + 1), (size_t)byte_count);
        checksum += sum_bytes(byte_destination, byte_count);

        memcpy(code_point_destination, code_point_source, (size_t)code_point_count * sizeof(int32_t));
        checksum += sum_code_points(code_point_destination, code_point_count);

        int32_t code_point_fill = 90 + (iteration % 11);
        for (int64_t index = 0; index < code_point_count; index += 1) {
            code_point_destination[index] = code_point_fill;
        }
        checksum += sum_code_points(code_point_destination, code_point_count);

        for (int64_t index = 0; index < byte_count; index += 1) {
            byte_move_buffer[index] = (int8_t)((index + iteration) % 97);
            code_point_move_buffer[index] = (int32_t)(65 + ((index + iteration) % 17));
        }

        memmove(
            byte_move_buffer + move_destination_start,
            byte_move_buffer,
            (size_t)move_count);
        checksum += sum_bytes(byte_move_buffer, byte_count);

        memmove(
            code_point_move_buffer + move_destination_start,
            code_point_move_buffer,
            (size_t)move_count * sizeof(int32_t));
        checksum += sum_code_points(code_point_move_buffer, code_point_count);
    }

    free(byte_source);
    free(byte_destination);
    free(code_point_source);
    free(code_point_destination);
    return checksum == 93749676 ? 0 : 1;
}
