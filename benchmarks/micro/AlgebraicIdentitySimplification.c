#include <stdint.h>
#include <unistd.h>

static int64_t normalize(int64_t value) {
    int64_t add = value + 0;
    int64_t multiply = add * 1;
    int64_t masked = multiply & -1;
    int64_t shifted = masked << 0;
    int64_t same_and = shifted & shifted;
    int64_t same_or = same_and | same_and;
    int64_t zero_xor = same_or ^ same_or;
    int64_t zero_and = value & 0;
    int64_t zero_multiply = value * 0;
    int64_t zero_subtract = value - value;
    int64_t all_ones = value | -1;
    return ((shifted ^ 0) + zero_xor + zero_and + zero_multiply + zero_subtract + (all_ones & 1)) - 1;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += normalize(((int64_t)i * 17) + (total % 97));
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
