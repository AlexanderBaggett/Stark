#include <stdint.h>
#include <string.h>

typedef struct {
    int8_t storage[512];
    int64_t read_position;
    int64_t write_position;
} FixedByteBuffer512;

static int64_t readable(const FixedByteBuffer512 *buffer) {
    return buffer->write_position <= buffer->read_position
        ? 0
        : buffer->write_position - buffer->read_position;
}

static int64_t writable(const FixedByteBuffer512 *buffer) {
    return buffer->write_position >= 512
        ? 0
        : 512 - buffer->write_position;
}

static int write_slice(FixedByteBuffer512 *buffer, const int8_t *source, int64_t count) {
    if (count > writable(buffer)) {
        return 0;
    }

    memcpy(buffer->storage + buffer->write_position, source, (size_t)count);
    buffer->write_position += count;
    return 1;
}

static int write_fill(FixedByteBuffer512 *buffer, int8_t value, int64_t count) {
    if (count > writable(buffer)) {
        return 0;
    }

    memset(buffer->storage + buffer->write_position, value, (size_t)count);
    buffer->write_position += count;
    return 1;
}

static void advance_read(FixedByteBuffer512 *buffer, int64_t count) {
    int64_t available = readable(buffer);
    if (count >= available) {
        buffer->read_position = 0;
        buffer->write_position = 0;
        return;
    }

    buffer->read_position += count;
}

static void compact(FixedByteBuffer512 *buffer) {
    int64_t available = readable(buffer);
    if (buffer->read_position == 0) {
        return;
    }

    if (available == 0) {
        buffer->read_position = 0;
        buffer->write_position = 0;
        return;
    }

    memmove(buffer->storage, buffer->storage + buffer->read_position, (size_t)available);
    buffer->read_position = 0;
    buffer->write_position = available;
}

static int64_t sum_bytes(const int8_t *values, int64_t count) {
    int64_t checksum = count;
    for (int64_t index = 0; index < count; index += 1) {
        checksum += values[index];
    }

    return checksum;
}

int main(void) {
    static const int iterations = 6000;
    const int8_t source[32] = {
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16
    };
    FixedByteBuffer512 buffer = {0};
    int64_t checksum = 0;

    for (int iteration = 0; iteration < iterations; iteration += 1) {
        buffer.read_position = 0;
        buffer.write_position = 0;

        if (!write_slice(&buffer, source, 32) || !write_fill(&buffer, 3, 96)) {
            return 1;
        }

        checksum += sum_bytes(buffer.storage + buffer.read_position, readable(&buffer));
        advance_read(&buffer, 48);
        compact(&buffer);

        if (!write_slice(&buffer, source, 32)) {
            return 1;
        }

        checksum += sum_bytes(buffer.storage + buffer.read_position, readable(&buffer));
    }

    return checksum == 7872000 ? 0 : 1;
}
