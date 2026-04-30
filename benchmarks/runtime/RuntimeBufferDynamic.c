#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    int8_t *data;
    int64_t length;
    int64_t capacity;
    int64_t read_position;
} DynamicByteBuffer;

static int64_t readable(const DynamicByteBuffer *buffer) {
    return buffer->length <= buffer->read_position
        ? 0
        : buffer->length - buffer->read_position;
}

static int reserve(DynamicByteBuffer *buffer, int64_t additional) {
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

static int write_slice(DynamicByteBuffer *buffer, const int8_t *source, int64_t count) {
    if (!reserve(buffer, count)) {
        return 0;
    }

    memcpy(buffer->data + buffer->length, source, (size_t)count);
    buffer->length += count;
    return 1;
}

static int write_fill(DynamicByteBuffer *buffer, int8_t value, int64_t count) {
    if (!reserve(buffer, count)) {
        return 0;
    }

    memset(buffer->data + buffer->length, value, (size_t)count);
    buffer->length += count;
    return 1;
}

static void advance_read(DynamicByteBuffer *buffer, int64_t count) {
    int64_t available = readable(buffer);
    if (count >= available) {
        buffer->length = 0;
        buffer->read_position = 0;
        return;
    }

    buffer->read_position += count;
}

static void compact(DynamicByteBuffer *buffer) {
    int64_t available = readable(buffer);
    if (buffer->read_position == 0) {
        return;
    }

    if (available == 0) {
        buffer->length = 0;
        buffer->read_position = 0;
        return;
    }

    memmove(buffer->data, buffer->data + buffer->read_position, (size_t)available);
    buffer->length = available;
    buffer->read_position = 0;
}

static int64_t sum_bytes(const int8_t *values, int64_t count) {
    int64_t checksum = count;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

int main(void) {
    static const int iterations = 1000;
    static const int chunks = 32;
    const int8_t source[16] = {
        1, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
        13, 14, 15, 16
    };
    int64_t checksum = 0;

    for (int iteration = 0; iteration < iterations; iteration += 1) {
        DynamicByteBuffer buffer = {0};

        for (int chunk = 0; chunk < chunks; chunk += 1) {
            if (!write_slice(&buffer, source, 16)) {
                free(buffer.data);
                return 1;
            }
        }

        advance_read(&buffer, 128);
        compact(&buffer);

        if (!write_fill(&buffer, 5, 64)) {
            free(buffer.data);
            return 1;
        }

        checksum += sum_bytes(buffer.data + buffer.read_position, readable(&buffer));
        free(buffer.data);
    }

    return checksum == 4032000 ? 0 : 1;
}
