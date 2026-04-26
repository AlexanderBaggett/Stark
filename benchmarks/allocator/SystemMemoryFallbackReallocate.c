#include <stdint.h>
#include <stdlib.h>

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 12000; i += 1) {
        void *allocation = malloc(16);
        if (allocation == NULL) {
            return 1;
        }

        ((uint8_t *)allocation)[0] = (uint8_t)i;

        void *next = realloc(allocation, 32);
        if (next == NULL) {
            free(allocation);
            return 1;
        }

        if (((uint8_t *)next)[0] != (uint8_t)i) {
            free(next);
            return 1;
        }

        checksum += i;
        free(next);
    }

    return checksum == 71994000 ? 0 : 1;
}
