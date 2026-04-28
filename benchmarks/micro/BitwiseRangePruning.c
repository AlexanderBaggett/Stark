#include <stdint.h>
#include <unistd.h>

static int64_t score(int32_t value, int64_t salt) {
    int32_t masked = value & 255;
    int32_t shifted = masked << 1;
    int32_t folded = shifted | (value & 3);
    int32_t forced = folded | 2048;

    if (forced == 0) {
        return salt - 1;
    }

    if (folded < 512) {
        return salt + folded;
    }

    return salt - folded;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += score(i % 1024, total % 97);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
