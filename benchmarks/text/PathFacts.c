#include <stdint.h>
#include <stddef.h>
#include <string.h>

struct path_facts {
    size_t length;
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
    size_t length = end;
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
        .length = length,
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

static int64_t add_facts(const char *path) {
    struct path_facts facts = get_path_facts(path);
    return (int64_t)facts.length
        + (int64_t)extension_length(facts)
        + (int64_t)base_name_length(facts)
        + (int64_t)facts.directory_length;
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 5000; i += 1) {
        checksum += add_facts("alpha/beta.txt");
        checksum += add_facts("alpha/beta/gamma.txt");
        checksum += add_facts("archive.tar.gz");
        checksum += add_facts("alpha/.hidden");
    }

    return checksum == 595000 ? 0 : 1;
}
