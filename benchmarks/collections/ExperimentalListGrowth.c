#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int32_t *items;
    size_t count;
    size_t capacity;
} IntList;

static int list_reserve(IntList *list, size_t additional) {
    size_t required = list->count + additional;
    if (required <= list->capacity) {
        return 1;
    }

    size_t next_capacity = list->capacity == 0 ? 8 : list->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int32_t *next = (int32_t *)realloc(list->items, next_capacity * sizeof(int32_t));
    if (next == NULL) {
        return 0;
    }

    list->items = next;
    list->capacity = next_capacity;
    return 1;
}

static int list_push(IntList *list, int32_t value) {
    if (!list_reserve(list, 1)) {
        return 0;
    }

    list->items[list->count] = value;
    list->count += 1;
    return 1;
}

static int list_pop(IntList *list, int32_t *value) {
    if (list->count == 0) {
        return 0;
    }

    list->count -= 1;
    *value = list->items[list->count];
    return 1;
}

int main(void) {
    IntList values = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 4096; i += 1) {
        if (!list_push(&values, i)) {
            free(values.items);
            return 1;
        }
    }

    if (values.count != 4096 || values.capacity < 4096) {
        free(values.items);
        return 2;
    }

    int32_t popped = 0;
    while (values.count > 0) {
        if (!list_pop(&values, &popped)) {
            free(values.items);
            return 3;
        }

        checksum += popped;
    }

    free(values.items);
    return checksum == 8386560 ? 0 : 4;
}
