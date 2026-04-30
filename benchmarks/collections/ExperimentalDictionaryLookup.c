#include <stdint.h>
#include <stdlib.h>

enum { CAPACITY = 4096 };

typedef struct {
    uint8_t *states;
    int32_t *keys;
    int32_t *values;
} IntDictionary;

static int dictionary_set(IntDictionary *dictionary, int32_t key, int32_t value) {
    size_t index = (uint32_t)key % CAPACITY;
    for (size_t probe = 0; probe < CAPACITY; probe += 1) {
        if (dictionary->states[index] == 0 || dictionary->keys[index] == key) {
            dictionary->states[index] = 1;
            dictionary->keys[index] = key;
            dictionary->values[index] = value;
            return 1;
        }

        index = (index + 1) % CAPACITY;
    }

    return 0;
}

static int dictionary_get(IntDictionary *dictionary, int32_t key, int32_t *value) {
    size_t index = (uint32_t)key % CAPACITY;
    for (size_t probe = 0; probe < CAPACITY; probe += 1) {
        if (dictionary->states[index] == 0) {
            return 0;
        }

        if (dictionary->keys[index] == key) {
            *value = dictionary->values[index];
            return 1;
        }

        index = (index + 1) % CAPACITY;
    }

    return 0;
}

int main(void) {
    IntDictionary values = {
        .states = (uint8_t *)calloc(CAPACITY, sizeof(uint8_t)),
        .keys = (int32_t *)malloc(CAPACITY * sizeof(int32_t)),
        .values = (int32_t *)malloc(CAPACITY * sizeof(int32_t))
    };

    if (values.states == NULL || values.keys == NULL || values.values == NULL) {
        free(values.states);
        free(values.keys);
        free(values.values);
        return 1;
    }

    for (int32_t i = 0; i < 2048; i += 1) {
        if (!dictionary_set(&values, i, i * 3)) {
            free(values.states);
            free(values.keys);
            free(values.values);
            return 1;
        }
    }

    int64_t checksum = 0;
    int32_t found = 0;
    for (int32_t i = 0; i < 100000; i += 1) {
        int32_t key = i % 2048;
        if (!dictionary_get(&values, key, &found)) {
            free(values.states);
            free(values.keys);
            free(values.values);
            return 2;
        }

        checksum += found;
    }

    free(values.states);
    free(values.keys);
    free(values.values);
    return checksum == 306154512 ? 0 : 3;
}
