#include <stdint.h>
#include <unistd.h>

static int64_t counter = 1;

__attribute__((noinline))
static int64_t mix(int64_t value) {
    return (value ^ (value >> 7)) + 17;
}

static int64_t accumulate(int64_t salt) {
    counter = (counter + salt) % 1000000007;
    int64_t first = counter;
    int64_t mixed = mix(first);
    int64_t second = counter;
    return mixed + first + second;
}

int main(void) {
    counter = ((int64_t)getpid() % 31) + 1;
    int64_t checksum = 0;

    for (int32_t i = 0; i < 200000; i += 1) {
        checksum += accumulate((int64_t)(i % 97));
        checksum %= 1000000007;
    }

    return checksum <= 0 ? 1 : 0;
}
