#include <stdint.h>
#include <unistd.h>

__attribute__((noinline)) static int32_t score(int64_t *ptr, int64_t seed) {
    if (ptr != 0) {
        if (ptr == 0) {
            return 1;
        }

        return (int32_t)((seed & 7) + 2);
    }

    return 0;
}

int main(void) {
    int64_t total = (int64_t)getpid();
    int64_t slot = total;
    int64_t *ptr = &slot;

    for (int32_t i = 0; i < 200000; i += 1) {
        total += score(ptr, total + (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
