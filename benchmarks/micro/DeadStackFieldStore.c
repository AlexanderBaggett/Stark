#include <stdint.h>
#include <unistd.h>

typedef struct Pair {
    int64_t left;
    int64_t right;
} Pair;

static int64_t accumulate(int64_t seed, int64_t salt) {
    Pair pair = { seed, salt };
    pair.left = seed + salt;
    pair.right = seed ^ salt;
    return ((seed * 3) + salt) % 1000000007;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 250000; i += 1) {
        total += accumulate(total, (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
