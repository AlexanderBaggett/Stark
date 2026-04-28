#include <stdint.h>
#include <string.h>
#include <unistd.h>

static int64_t score(int64_t salt) {
    if (strlen("stark-performance") == 17) {
        salt = (salt + 51) % 1000003;
    } else {
        salt -= 100000;
    }

    if ((sizeof("llvm-output") - 1) != 11) {
        return salt - 77777;
    }

    return ((salt + 11) * 17) % 1000003;
}

int main(void) {
    int64_t total = (int64_t)getpid();

    for (int32_t i = 0; i < 200000; i += 1) {
        total += score(total + (int64_t)i);
        total %= 1000000007;
    }

    return total <= 0 ? 1 : 0;
}
