#include <stdint.h>
#include <unistd.h>

typedef struct Pair {
    int64_t left;
    int64_t right;
} Pair;

static int64_t accumulate(int64_t seed, int64_t salt) {
    Pair pair = { seed, salt };
    pair.left = pair.left + pair.right;
    int64_t first = pair.left;
    pair.left = first ^ (first >> 7);
    return pair.left + first + pair.right;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += accumulate(total % 1000000007, (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
