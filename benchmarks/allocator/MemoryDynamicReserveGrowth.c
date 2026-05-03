#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    int8_t *data;
    int64_t length;
    int64_t capacity;
} ByteBuffer;

typedef struct {
    int32_t *data;
    int64_t length;
    int64_t capacity;
} CodePointBuffer;

static int reserve_bytes(ByteBuffer *buffer, int64_t additional) {
    int64_t required = buffer->length + additional;
    if (required <= buffer->capacity) {
        return 1;
    }

    int64_t next_capacity = buffer->capacity == 0 ? 16 : buffer->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int8_t *next = (int8_t *)realloc(buffer->data, (size_t)next_capacity);
    if (next == NULL) {
        return 0;
    }

    buffer->data = next;
    buffer->capacity = next_capacity;
    return 1;
}

static int reserve_code_points(CodePointBuffer *buffer, int64_t additional) {
    int64_t required = buffer->length + additional;
    if (required <= buffer->capacity) {
        return 1;
    }

    int64_t next_capacity = buffer->capacity == 0 ? 8 : buffer->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int32_t *next = (int32_t *)realloc(buffer->data, (size_t)next_capacity * sizeof(int32_t));
    if (next == NULL) {
        return 0;
    }

    buffer->data = next;
    buffer->capacity = next_capacity;
    return 1;
}

static int append_bytes(ByteBuffer *buffer, const int8_t *source, int64_t count) {
    if (!reserve_bytes(buffer, count)) {
        return 0;
    }

    memcpy(buffer->data + buffer->length, source, (size_t)count);
    buffer->length += count;
    return 1;
}

static int append_fill_bytes(ByteBuffer *buffer, int8_t value, int64_t count) {
    if (!reserve_bytes(buffer, count)) {
        return 0;
    }

    memset(buffer->data + buffer->length, value, (size_t)count);
    buffer->length += count;
    return 1;
}

static int append_code_points(CodePointBuffer *buffer, const int32_t *source, int64_t count) {
    if (!reserve_code_points(buffer, count)) {
        return 0;
    }

    memcpy(buffer->data + buffer->length, source, (size_t)count * sizeof(int32_t));
    buffer->length += count;
    return 1;
}

static int append_fill_code_points(CodePointBuffer *buffer, int32_t value, int64_t count) {
    if (!reserve_code_points(buffer, count)) {
        return 0;
    }

    for (int64_t index = 0; index < count; index += 1) {
        buffer->data[buffer->length + index] = value;
    }

    buffer->length += count;
    return 1;
}

static int64_t sum_bytes(const int8_t *values, int64_t count) {
    int64_t checksum = count;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

static int64_t sum_code_points(const int32_t *values, int64_t count) {
    int64_t checksum = count;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

int main(void) {
    static const int iterations = 800;
    static const int chunks = 32;
    const int8_t byte_source[16] = {
        1, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
        13, 14, 15, 16
    };
    const int32_t code_point_source[8] = {
        65, 66, 67, 68,
        69, 70, 71, 72
    };
    int64_t checksum = 0;

    for (int iteration = 0; iteration < iterations; iteration += 1) {
        ByteBuffer bytes = {0};
        CodePointBuffer code_points = {0};

        for (int chunk = 0; chunk < chunks; chunk += 1) {
            if (!append_bytes(&bytes, byte_source, 16) ||
                !append_code_points(&code_points, code_point_source, 8)) {
                free(bytes.data);
                free(code_points.data);
                return 1;
            }
        }

        if (!append_fill_bytes(&bytes, 7, 32) ||
            !append_fill_code_points(&code_points, 90, 32)) {
            free(bytes.data);
            free(code_points.data);
            return 1;
        }

        checksum += sum_bytes(bytes.data, bytes.length);
        checksum += sum_code_points(code_points.data, code_points.length);
        free(bytes.data);
        free(code_points.data);
    }

    return checksum == 20659200 ? 0 : 1;
}
