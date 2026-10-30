#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <stdio.h>

int main(void) {
    static const int iterations = 64;
    for (int iteration = 0; iteration < iterations; iteration += 1) {
        if (fputs("small", stdout) < 0
            || fputs(" line\n", stdout) < 0
            || fputs("wide \xCE\xB1\n", stdout) < 0
            || fputs("buffer payload 0123456789ABCDEF\n", stdout) < 0
            || fputs("err\n", stderr) < 0) {
            return 1;
        }
    }

    if (fflush(stdout) != 0 || fflush(stderr) != 0) {
        return 2;
    }

    return 0;
}
