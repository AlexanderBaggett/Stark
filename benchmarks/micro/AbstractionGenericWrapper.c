#include <stdint.h>
#include <unistd.h>

static int64_t identity_i64(int64_t value) {
    return value;
}

static int64_t mix_core(int64_t value, int64_t salt) {
    return (((value * 31) + salt) ^ (value >> 3)) % 1000003;
}

static int64_t mix(int64_t value, int64_t salt, int32_t tag) {
    (void)tag;
    return mix_core(identity_i64(value), identity_i64(salt));
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += mix((int64_t)i, total % 97, i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
