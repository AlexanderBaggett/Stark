#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int32_t *items;
    size_t count;
    size_t capacity;
} IntStack;

static int stack_reserve(IntStack *stack, size_t additional) {
    size_t required = stack->count + additional;
    if (required <= stack->capacity) {
        return 1;
    }

    size_t next_capacity = stack->capacity == 0 ? 8 : stack->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int32_t *next = (int32_t *)realloc(stack->items, next_capacity * sizeof(int32_t));
    if (next == NULL) {
        return 0;
    }

    stack->items = next;
    stack->capacity = next_capacity;
    return 1;
}

static int stack_push(IntStack *stack, int32_t value) {
    if (!stack_reserve(stack, 1)) {
        return 0;
    }

    stack->items[stack->count] = value;
    stack->count += 1;
    return 1;
}

static int stack_pop(IntStack *stack, int32_t *value) {
    if (stack->count == 0) {
        return 0;
    }

    stack->count -= 1;
    *value = stack->items[stack->count];
    return 1;
}

int main(void) {
    IntStack values = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 4096; i += 1) {
        if (!stack_push(&values, i)) {
            free(values.items);
            return 1;
        }
    }

    if (values.count != 4096) {
        free(values.items);
        return 2;
    }

    int32_t popped = 0;
    while (values.count > 0) {
        if (!stack_pop(&values, &popped)) {
            free(values.items);
            return 3;
        }

        checksum += popped;
    }

    free(values.items);
    return checksum == 8386560 ? 0 : 4;
}
