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
    return 1;
}

int main(void) {
    IntQueue queue = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 32768; i += 1) {
        if (!queue_enqueue(&queue, i)) {
            free(queue.items);
            return 1;
        }
    }

    int32_t value = 0;
    while (queue.count > 0) {
        if (!queue_dequeue(&queue, &value)) {
            free(queue.items);
            return 2;
        }

        checksum += value;
    }

    free(queue.items);
    return checksum == 536854528 ? 0 : 3;
}
