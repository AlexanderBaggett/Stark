#include <stdint.h>
#include <stdlib.h>

enum { CAPACITY = 8192 };

typedef struct {
    uint8_t *states;
    int32_t *keys;
    int32_t *values;
    size_t count;
} IntDictionary;

static int dictionary_init(IntDictionary *dictionary) {
    dictionary->states = (uint8_t *)calloc(CAPACITY, sizeof(uint8_t));
    dictionary->keys = (int32_t *)malloc(CAPACITY * sizeof(int32_t));
    dictionary->values = (int32_t *)malloc(CAPACITY * sizeof(int32_t));
    dictionary->count = 0;
    return dictionary->states != NULL && dictionary->keys != NULL && dictionary->values != NULL;
}

static void dictionary_free(IntDictionary *dictionary) {
    free(dictionary->states);
    free(dictionary->keys);
    free(dictionary->values);
}

static int dictionary_set(IntDictionary *dictionary, int32_t key, int32_t value) {
    size_t index = (uint32_t)key % CAPACITY;
    for (size_t probe = 0; probe < CAPACITY; probe += 1) {
        uint8_t state = dictionary->states[index];
        if (state == 0 || state == 2 || (state == 1 && dictionary->keys[index] == key)) {
            if (state != 1) {
                dictionary->count += 1;
            }

            dictionary->states[index] = 1;
            dictionary->keys[index] = key;
            dictionary->values[index] = value;
            return 1;
        }

        index = (index + 1) % CAPACITY;
    }

    return 0;
}

static int dictionary_remove(IntDictionary *dictionary, int32_t key) {
    size_t index = (uint32_t)key % CAPACITY;
    for (size_t probe = 0; probe < CAPACITY; probe += 1) {
        uint8_t state = dictionary->states[index];
        if (state == 0) {
            return 0;
        }

        if (state == 1 && dictionary->keys[index] == key) {
            dictionary->states[index] = 2;
            dictionary->count -= 1;
            return 1;
        }

        index = (index + 1) % CAPACITY;
    }

    return 0;
}

int main(void) {
    IntDictionary values = {0};
    if (!dictionary_init(&values)) {
        dictionary_free(&values);
        return 1;
    }

    for (int32_t i = 0; i < 4096; i += 1) {
        if (!dictionary_set(&values, i, i)) {
            dictionary_free(&values);
            return 2;
        }
    }

    int64_t checksum = 0;
    for (int32_t i = 0; i < 4096; i += 1) {
        if (!dictionary_remove(&values, i)) {
            dictionary_free(&values);
            return 3;
        }

        checksum += i;
    }

    int status = values.count == 0 && checksum == 8386560 ? 0 : 4;
    dictionary_free(&values);
    return status;
}
