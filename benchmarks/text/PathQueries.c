#include <stdint.h>
#include <stddef.h>
#include <string.h>

struct path_facts {
    size_t end;
    size_t segment_start;
    size_t extension_start;
    size_t directory_length;
    int has_extension;
};

static int is_separator(char value) {
    return value == '/';
}

static struct path_facts get_path_facts(const char *path) {
    size_t end = strlen(path);
    while (end > 1 && is_separator(path[end - 1])) {
        end -= 1;
    }

    size_t separator = (size_t)-1;
    for (size_t index = end; index > 0; index -= 1) {
        if (is_separator(path[index - 1])) {
            separator = index - 1;
            break;
        }
    }

    size_t segment_start = separator == (size_t)-1 ? 0 : separator + 1;
    size_t directory_length = 0;
    if (separator == 0) {
        directory_length = 1;
    } else if (separator != (size_t)-1) {
        directory_length = separator;
    }

    size_t extension_start = end;
    int has_extension = 0;
    for (size_t index = end; index > segment_start + 1; index -= 1) {
        if (path[index - 1] == '.') {
            extension_start = index - 1;
            has_extension = 1;
            break;
        }
    }

    return (struct path_facts){
        .end = end,
        .segment_start = segment_start,
        .extension_start = extension_start,
        .directory_length = directory_length,
        .has_extension = has_extension,
    };
}

static size_t extension_length(struct path_facts facts) {
    return facts.has_extension ? facts.end - facts.extension_start : 0;
}

static size_t base_name_length(struct path_facts facts) {
    if (facts.segment_start >= facts.end) {
        return 0;
    }

    return (facts.has_extension ? facts.extension_start : facts.end) - facts.segment_start;
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 10000; i += 1) {
        struct path_facts beta = get_path_facts("alpha/beta.txt");
        checksum += (int64_t)extension_length(beta);
        checksum += (int64_t)base_name_length(beta);
        checksum += (int64_t)beta.directory_length;

        struct path_facts archive = get_path_facts("archive.tar.gz");
        checksum += (int64_t)extension_length(archive);
        checksum += (int64_t)base_name_length(archive);
        checksum += (int64_t)archive.directory_length;
    }

    return checksum == 270000 ? 0 : 1;
}
