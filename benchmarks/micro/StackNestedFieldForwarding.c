#include <stdint.h>
#include <unistd.h>

typedef struct Inner {
    int64_t value;
    int64_t salt;
} Inner;

typedef struct Outer {
    Inner left;
    Inner right;
} Outer;

static int64_t accumulate(int64_t seed, int64_t salt) {
    Outer outer = {
        { seed, salt },
        { salt, seed }
    };
    outer.left.value = outer.left.value + outer.right.salt;
    int64_t first = outer.left.value;
    outer.right.value = first ^ (outer.right.value >> 5);
    return outer.left.value + outer.right.value + outer.left.salt;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += accumulate(total % 1000000007, (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
