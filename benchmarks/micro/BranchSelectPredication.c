#include <stdint.h>
#include <unistd.h>

__attribute__((noinline)) static int64_t choose_value(int flag, int64_t left, int64_t right) {
    return flag ? left : right;
}

int main(void) {
    int64_t seed = (int64_t)getpid();
    int64_t total = seed & 1023;

    for (int32_t i = 0; i < 500000; i += 1) {
        int64_t wide = (int64_t)i;
        int flag = ((wide + total) & 1) == 0;
        total += choose_value(flag, wide + 3, total - wide);
        total %= 1000000007;
    }

    return total == 0 ? 1 : 0;
}
