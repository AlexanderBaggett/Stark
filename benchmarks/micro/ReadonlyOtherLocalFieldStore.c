#include <stdint.h>
#include <unistd.h>

typedef struct Box {
    int64_t value;
} Box;

__attribute__((noinline))
static int64_t read_value(const Box *box) {
    return box->value;
}

static int64_t accumulate(int64_t seed, int64_t salt) {
    Box left = { seed };
    Box right = { salt };
    left.value = seed + salt;
    int64_t observed = read_value(&right);
    left.value = (seed ^ observed) + 17;
    return left.value + observed;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += accumulate(total % 1000000007, (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
