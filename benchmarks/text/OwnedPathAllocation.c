#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static int is_separator(char value) {
    return value == '/';
}

static char *join_path(const char *left, const char *right) {
    size_t left_length = strlen(left);
    size_t right_length = strlen(right);
    size_t right_start = 0;
    int insert_separator = 0;

    if (left_length > 0 && right_length > 0) {
        if (is_separator(left[left_length - 1])) {
            if (is_separator(right[0])) {
                right_start = 1;
            }
        } else if (!is_separator(right[0])) {
            insert_separator = 1;
        }
    }

    size_t right_copy_length = right_length - right_start;
    size_t length = left_length + right_copy_length + (insert_separator ? 1 : 0);
    char *path = (char *)malloc(length + 1);
    if (path == NULL) {
        return NULL;
    }

    size_t write = 0;
    memcpy(path + write, left, left_length);
    write += left_length;
    if (insert_separator) {
        path[write] = '/';
        write += 1;
    }
    memcpy(path + write, right + right_start, right_copy_length);
    write += right_copy_length;
    path[write] = '\0';
    return path;
}

struct path_facts {
    size_t length;
    size_t end;
    size_t segment_start;
    size_t extension_start;
    size_t directory_length;
    int has_extension;
};

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

static size_t directory_name_length(struct path_facts facts) {
    return facts.directory_length;
}

static int add_path_facts(char *path, int64_t *checksum) {
    if (path == NULL) {
        return 0;
    }

    struct path_facts facts = get_path_facts(path);
    *checksum += (int64_t)facts.length;
    *checksum += (int64_t)extension_length(facts);
    *checksum += (int64_t)base_name_length(facts);
    *checksum += (int64_t)directory_name_length(facts);
    free(path);
    return 1;
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 2000; i += 1) {
        if (!add_path_facts(join_path("alpha", "beta.txt"), &checksum)) {
            return 1;
        }

        if (!add_path_facts(join_path("alpha/", "/beta.txt"), &checksum)) {
            return 2;
        }
    }

    return checksum == 108000 ? 0 : 3;
}
