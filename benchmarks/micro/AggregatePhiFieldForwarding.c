#include <stdint.h>
#include <stdbool.h>
#include <unistd.h>

typedef struct Pair {
    int64_t value;
    int64_t tag;
} Pair;

static int64_t read_value(bool flag, int64_t value, int64_t tag) {
    Pair pair = flag
        ? (Pair){ value, tag }
        : (Pair){ value, tag + 1 };
    return pair.value;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 300000; i += 1) {
        total += read_value((i & 1) == 0, total % 1000000007, (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
