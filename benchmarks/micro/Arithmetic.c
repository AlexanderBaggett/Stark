// stark-bench: skip-c-windows
#include <stdint.h>
#include <unistd.h>

int main(void) {
    int64_t seed = (int64_t)getpid();
    int64_t total = seed % 97;

    for (int32_t i = 0; i < 200000; i += 1) {
        int64_t value = (int64_t)i + total;
        total = (total + (value * 3) - (value / 3) + (value % 11)) % 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
