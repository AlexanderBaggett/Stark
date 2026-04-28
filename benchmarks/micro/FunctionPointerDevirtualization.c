#include <stdint.h>

__attribute__((noinline)) static int64_t mix(int64_t value, int64_t salt) {
    return ((value * 31) + salt) % 1000003;
}

int main(void) {
    int64_t total = 17;

    for (int32_t i = 0; i < 200000; i += 1) {
        int64_t (*op)(int64_t, int64_t) = mix;
        if ((i % 2) != 0) {
            op = mix;
        }

        total += op((int64_t)i, total % 97);
        total %= 1000000007;
    }

    return total == 420921655 ? 0 : 1;
}
