#include <stdint.h>
#include <unistd.h>

static int64_t score(int64_t value, int64_t salt) {
    return (((value * 31) + salt) ^ (value >> 3)) % 1000003;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += score((int64_t)i, total % 97);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
