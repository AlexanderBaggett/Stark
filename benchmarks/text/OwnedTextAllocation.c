#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char *format_i32_ascii(int32_t value, size_t *length) {
    int required = snprintf(NULL, 0, "%d", value);
    if (required < 0) {
        return NULL;
    }

    char *text = (char *)malloc((size_t)required + 1);
    if (text == NULL) {
        return NULL;
    }

    snprintf(text, (size_t)required + 1, "%d", value);
    *length = (size_t)required;
    return text;
}

static int32_t *format_i32_unicode(int32_t value, size_t *length) {
    size_t ascii_length = 0;
    char *ascii = format_i32_ascii(value, &ascii_length);
    if (ascii == NULL) {
        return NULL;
    }

    int32_t *unicode = (int32_t *)malloc(ascii_length * sizeof(int32_t));
    if (unicode == NULL) {
        free(ascii);
        return NULL;
    }

    for (size_t i = 0; i < ascii_length; i += 1) {
        unicode[i] = (int32_t)ascii[i];
    }

    free(ascii);
    *length = ascii_length;
    return unicode;
}

static char *concat_score_ascii(const char *digits, size_t digits_length, size_t *length) {
    static const char prefix[] = "Score: ";
    size_t prefix_length = sizeof(prefix) - 1;
    char *text = (char *)malloc(prefix_length + digits_length + 1);
    if (text == NULL) {
        return NULL;
    }

    memcpy(text, prefix, prefix_length);
    memcpy(text + prefix_length, digits, digits_length);
    text[prefix_length + digits_length] = '\0';
    *length = prefix_length + digits_length;
    return text;
}

static int32_t *concat_score_unicode(int32_t *digits, size_t digits_length, size_t *length) {
    static const char prefix[] = "Score: ";
    size_t prefix_length = sizeof(prefix) - 1;
    int32_t *text = (int32_t *)malloc((prefix_length + digits_length) * sizeof(int32_t));
    if (text == NULL) {
        return NULL;
    }

    for (size_t i = 0; i < prefix_length; i += 1) {
        text[i] = (int32_t)prefix[i];
    }

    for (size_t i = 0; i < digits_length; i += 1) {
        text[prefix_length + i] = digits[i];
    }

    *length = prefix_length + digits_length;
    return text;
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 1000; i += 1) {
        size_t ascii_length = 0;
        char *ascii = format_i32_ascii(i, &ascii_length);
        if (ascii == NULL) {
            return 1;
        }
        checksum += (int64_t)ascii_length;

        size_t unicode_length = 0;
        int32_t *unicode = format_i32_unicode(i, &unicode_length);
        if (unicode == NULL) {
            free(ascii);
            return 2;
        }
        checksum += (int64_t)unicode_length;

        size_t label_length = 0;
        char *label = concat_score_ascii(ascii, ascii_length, &label_length);
        if (label == NULL) {
            free(unicode);
            free(ascii);
            return 3;
        }
        checksum += (int64_t)label_length;

        size_t unicode_label_length = 0;
        int32_t *unicode_label = concat_score_unicode(unicode, unicode_length, &unicode_label_length);
        if (unicode_label == NULL) {
            free(label);
            free(unicode);
            free(ascii);
            return 4;
        }
        checksum += (int64_t)unicode_label_length;

        free(unicode_label);
        free(label);
        free(unicode);
        free(ascii);
    }

    return checksum == 25560 ? 0 : 5;
}
