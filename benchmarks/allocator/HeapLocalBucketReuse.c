#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int64_t value;
} Box;

int main(void) {
    enum { BATCH = 128 };
    Box *boxes[BATCH] = {0};
    int64_t checksum = 0;

    for (int32_t start = 0; start < 20000; start += BATCH) {
        int32_t count = 20000 - start;
        if (count > BATCH) {
            count = BATCH;
        }

        for (int32_t index = 0; index < count; index += 1) {
            Box *box = (Box *)malloc(sizeof(Box));
            if (box == NULL) {
                return 1;
            }

            box->value = start + index;
            boxes[index] = box;
        }

        for (int32_t index = 0; index < count; index += 1) {
            checksum += boxes[index]->value;
            free(boxes[index]);
            boxes[index] = NULL;
        }
    }

    return checksum == 199990000 ? 0 : 1;
}
