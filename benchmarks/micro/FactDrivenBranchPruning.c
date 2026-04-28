#include <stdint.h>
#include <unistd.h>

static int64_t score(int32_t value, int64_t salt) {
    if (value < 20) {
        return (((int64_t)value * 37) + salt) % 1000003;
    }

    return salt - 1000;
}

static int64_t switch_score(int32_t value, int64_t salt) {
    switch (value) {
        case 10:
            return salt + 10;
        case 40:
            return salt + 40;
        case 41:
            return salt + 41;
        default:
            return salt - 3;
    }
}

static int64_t nested_score(int32_t value, int64_t salt) {
    if (value < 10) {
        if (value >= 10) {
            return salt - 10000;
        }

        return (((int64_t)value * 13) + salt) % 1000003;
    }

    return salt + (int64_t)value;
}

static int64_t modulo_score(int32_t value, int64_t salt) {
    int32_t slot = value % 8;
    if (slot >= 8) {
        return salt - 4000;
    }

    return salt + (int64_t)slot;
}

static int64_t divide_score(int32_t value, int64_t salt) {
    int32_t bucket = value / 10;
    if (bucket > 10) {
        return salt - 5000;
    }

    return salt + (int64_t)bucket;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += score(i % 11, total % 97);
        total += switch_score(10 + (i % 3), total % 31);
        total += nested_score(i % 21, total % 53);
        total += modulo_score(i % 101, total % 43);
        total += divide_score(i % 101, total % 47);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
