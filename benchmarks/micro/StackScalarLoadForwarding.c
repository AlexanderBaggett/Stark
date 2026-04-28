#include <stdint.h>
#include <unistd.h>

static int64_t accumulate(int64_t seed, int64_t salt) {
    int64_t local = seed;
    local = local + salt;
    int64_t first = local;
    local = first ^ (first >> 7);
    return local + first;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += accumulate(total % 1000000007, (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
