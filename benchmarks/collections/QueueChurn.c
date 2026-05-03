#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int32_t *items;
    size_t count;
    size_t capacity;
    size_t head;
} IntQueue;

static int queue_grow(IntQueue *queue) {
    size_t next_capacity = queue->capacity == 0 ? 8 : queue->capacity * 2;
    int32_t *next = (int32_t *)malloc(next_capacity * sizeof(int32_t));
    if (next == NULL) {
        return 0;
    }

    for (size_t i = 0; i < queue->count; i += 1) {
        next[i] = queue->items[(queue->head + i) % queue->capacity];
    }

    free(queue->items);
    queue->items = next;
    queue->capacity = next_capacity;
    queue->head = 0;
    return 1;
}

static int queue_enqueue(IntQueue *queue, int32_t value) {
    if (queue->count == queue->capacity && !queue_grow(queue)) {
        return 0;
    }

    size_t index = (queue->head + queue->count) % queue->capacity;
    queue->items[index] = value;
    queue->count += 1;
    return 1;
}

static int queue_dequeue(IntQueue *queue, int32_t *value) {
    if (queue->count == 0) {
        return 0;
    }

    *value = queue->items[queue->head];
    queue->head = (queue->head + 1) % queue->capacity;
    queue->count -= 1;
    if (queue->count == 0) {
        queue->head = 0;
    }

    return 1;
}

int main(void) {
    IntQueue queue = {0};
    int64_t checksum = 0;
    int32_t next = 0;

    for (int32_t cycle = 0; cycle < 256; cycle += 1) {
        for (int32_t i = 0; i < 64; i += 1) {
            if (!queue_enqueue(&queue, next)) {
                free(queue.items);
                return 1;
            }

            next += 1;
        }

        int32_t value = 0;
        for (int32_t i = 0; i < 32; i += 1) {
            if (!queue_dequeue(&queue, &value)) {
                free(queue.items);
                return 2;
            }

            checksum += value;
        }

        for (int32_t i = 0; i < 32; i += 1) {
            if (!queue_enqueue(&queue, next)) {
                free(queue.items);
                return 3;
            }

            next += 1;
        }

        for (int32_t i = 0; i < 64; i += 1) {
            if (!queue_dequeue(&queue, &value)) {
                free(queue.items);
                return 4;
            }

            checksum += value;
        }
    }

    int status = queue.count == 0 && next == 24576 && checksum == 301977600 ? 0 : 5;
    free(queue.items);
    return status;
}
