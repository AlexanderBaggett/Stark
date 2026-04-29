#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int32_t *items;
    size_t count;
    size_t capacity;
} IntQueue;

static int queue_reserve(IntQueue *queue, size_t additional) {
    size_t required = queue->count + additional;
    if (required <= queue->capacity) {
        return 1;
    }

    size_t next_capacity = queue->capacity == 0 ? 8 : queue->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int32_t *next = (int32_t *)realloc(queue->items, next_capacity * sizeof(int32_t));
    if (next == NULL) {
        return 0;
    }

    queue->items = next;
    queue->capacity = next_capacity;
    return 1;
}

static int queue_enqueue(IntQueue *queue, int32_t value) {
    if (!queue_reserve(queue, 1)) {
        return 0;
    }

    queue->items[queue->count] = value;
    queue->count += 1;
    return 1;
}

int main(void) {
    IntQueue queue = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 4096; i += 1) {
        if (!queue_enqueue(&queue, i)) {
            free(queue.items);
            return 1;
        }
    }

    if (queue.count != 4096 || queue.capacity < 4096) {
        free(queue.items);
        return 2;
    }

    for (size_t head = 0; head < queue.count; head += 1) {
        checksum += queue.items[head];
    }

    free(queue.items);
    return checksum == 8386560 ? 0 : 4;
}
