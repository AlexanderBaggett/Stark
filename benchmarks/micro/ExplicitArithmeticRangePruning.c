#include <stdint.h>
#include <unistd.h>

static int32_t saturating_add_i32(int32_t left, int32_t right) {
    int64_t result = (int64_t)left + (int64_t)right;
    if (result > INT32_MAX) {
        return INT32_MAX;
    }

    if (result < INT32_MIN) {
        return INT32_MIN;
    }

    return (int32_t)result;
}

static int64_t score(int32_t left, int32_t right, int64_t salt) {
    int32_t saturated = saturating_add_i32(left, right);
    int32_t wrapped = left + right;

    if (saturated > 15) {
        return salt - 10000;
    }

    if (wrapped > 15) {
        return salt - 20000;
    }

    return (((int64_t)saturated * 37) + ((int64_t)wrapped * 17) + salt) % 1000003;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += score(i % 11, i % 6, total % 97);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
