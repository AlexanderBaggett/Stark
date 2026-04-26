#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int32_t *items;
    size_t count;
    size_t capacity;
} IntList;

static int list_push(IntList *list, int32_t value) {
    if (list->count == list->capacity) {
        size_t next_capacity = list->capacity == 0 ? 8 : list->capacity * 2;
        int32_t *next = (int32_t *)realloc(list->items, next_capacity * sizeof(int32_t));
        if (next == NULL) {
            return 0;
        }

        list->items = next;
        list->capacity = next_capacity;
    }

    list->items[list->count] = value;
    list->count += 1;
    return 1;
}

int main(void) {
    IntList values = {0};

    for (int32_t i = 0; i < 4096; i += 1) {
        if (!list_push(&values, i)) {
            free(values.items);
            return 1;
        }
    }

    int64_t checksum = 0;
    for (int32_t i = 0; i < 4096; i += 1) {
        checksum += values.items[i];
    }

    free(values.items);
    return checksum == 8386560 ? 0 : 2;
}
