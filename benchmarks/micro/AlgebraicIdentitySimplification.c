#include <stdint.h>
#include <unistd.h>

static int64_t normalize(int64_t value) {
    int64_t add = value + 0;
    int64_t multiply = add * 1;
    int64_t masked = multiply & -1;
    int64_t shifted = masked << 0;
    int64_t divided = shifted / 1;
    int64_t right_shifted = divided >> 0;
    int64_t same_and = shifted & shifted;
    int64_t same_or = same_and | same_and;
    int64_t zero_xor = same_or ^ same_or;
    int64_t zero_and = value & 0;
    int64_t zero_multiply = value * 0;
    int64_t zero_subtract = value - value;
    int64_t zero_modulo = value % 1;
    int64_t all_ones = value | -1;
    return ((right_shifted ^ 0) + zero_xor + zero_and + zero_multiply + zero_subtract + zero_modulo + (all_ones & 1)) - 1;
}

static int64_t normalize_slot(int32_t slot, int64_t salt) {
    int32_t modulo = slot % 8;
    int32_t divided = slot / 8;
    return salt + (int64_t)modulo + (int64_t)divided;
}

static int64_t normalize_nonzero_slot(int32_t slot, int64_t salt) {
    int32_t divided = slot / slot;
    int32_t modulo = slot % slot;
    return salt + (int64_t)divided + (int64_t)modulo;
}

static int64_t normalize_comparisons(int32_t value, int64_t salt) {
    int32_t copy = value;
    int32_t equal = value == copy ? 1 : 100;
    int32_t not_equal = value != copy ? 100 : 1;
    int32_t less = value < copy ? 100 : 1;
    int32_t less_or_equal = value <= copy ? 1 : 100;
    int32_t greater = value > copy ? 100 : 1;
    int32_t greater_or_equal = value >= copy ? 1 : 100;
    return salt + (int64_t)(equal + not_equal + less + less_or_equal + greater + greater_or_equal);
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += normalize(((int64_t)i * 17) + (total % 97));
        total += normalize_slot(i & 7, total % 41);
        total += normalize_nonzero_slot((i & 7) + 1, total % 43);
        total += normalize_comparisons(i, total % 47);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
