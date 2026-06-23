#include <curl/curl.h>
#include <stdlib.h>
#include <string.h>

#define STARK_CURL_BODY_TOO_SMALL (-1000)
#define STARK_CURL_HEADERS_TOO_SMALL (-1001)
#define STARK_CURL_OUT_OF_MEMORY (-1002)
#define STARK_CURL_INVALID_ARGUMENT (-1003)

typedef struct stark_curl_fixed_buffer {
    unsigned char *data;
    size_t capacity;
    size_t length;
    int overflow_code;
} stark_curl_fixed_buffer;

typedef struct stark_curl_grow_buffer {
    unsigned char *data;
    size_t length;
    size_t capacity;
} stark_curl_grow_buffer;

typedef struct stark_curl_response {
    long status_code;
    stark_curl_grow_buffer body;
    stark_curl_grow_buffer headers;
} stark_curl_response;

void stark_curl_response_free(stark_curl_response *response);

static int stark_curl_global_ready = 0;

static int stark_curl_ensure_global(void)
{
    if (stark_curl_global_ready) {
        return CURLE_OK;
    }

    CURLcode code = curl_global_init(CURL_GLOBAL_DEFAULT);
    if (code != CURLE_OK) {
        return (int)code;
    }

    stark_curl_global_ready = 1;
    return CURLE_OK;
}

static size_t stark_curl_write_fixed_body(char *ptr, size_t size, size_t nmemb, void *userdata)
{
    stark_curl_fixed_buffer *buffer = (stark_curl_fixed_buffer *)userdata;
    size_t byte_count = size * nmemb;

    if (byte_count == 0) {
        return 0;
    }

    if (buffer == NULL || buffer->data == NULL || byte_count > buffer->capacity - buffer->length) {
        if (buffer != NULL) {
            buffer->overflow_code = STARK_CURL_BODY_TOO_SMALL;
        }

        return 0;
    }

    memcpy(buffer->data + buffer->length, ptr, byte_count);
    buffer->length += byte_count;
    return byte_count;
}

static size_t stark_curl_write_fixed_headers(char *ptr, size_t size, size_t nmemb, void *userdata)
{
    stark_curl_fixed_buffer *buffer = (stark_curl_fixed_buffer *)userdata;
    size_t byte_count = size * nmemb;

    if (byte_count == 0) {
        return 0;
    }

    if (buffer == NULL || buffer->data == NULL || byte_count > buffer->capacity - buffer->length) {
        if (buffer != NULL) {
            buffer->overflow_code = STARK_CURL_HEADERS_TOO_SMALL;
        }

        return 0;
    }

    memcpy(buffer->data + buffer->length, ptr, byte_count);
    buffer->length += byte_count;
    return byte_count;
}

static int stark_curl_grow(stark_curl_grow_buffer *buffer, size_t additional)
{
    if (additional == 0) {
        return 1;
    }

    if (buffer == NULL || additional > ((size_t)-1) - buffer->length) {
        return 0;
    }

    size_t required = buffer->length + additional;
    if (required <= buffer->capacity) {
        return 1;
    }

    size_t next_capacity = buffer->capacity == 0 ? 4096 : buffer->capacity;
    while (next_capacity < required) {
        if (next_capacity > ((size_t)-1) / 2) {
            next_capacity = required;
            break;
        }

        next_capacity *= 2;
    }

    unsigned char *next = (unsigned char *)realloc(buffer->data, next_capacity);
    if (next == NULL) {
        return 0;
    }

    buffer->data = next;
    buffer->capacity = next_capacity;
    return 1;
}

static size_t stark_curl_write_grow(char *ptr, size_t size, size_t nmemb, void *userdata)
{
    stark_curl_grow_buffer *buffer = (stark_curl_grow_buffer *)userdata;
    size_t byte_count = size * nmemb;

    if (byte_count == 0) {
        return 0;
    }

    if (!stark_curl_grow(buffer, byte_count)) {
        return 0;
    }

    memcpy(buffer->data + buffer->length, ptr, byte_count);
    buffer->length += byte_count;
    return byte_count;
}

static int stark_curl_prepare_common(
    CURL *curl,
    const char *url,
    long timeout_milliseconds,
    int follow_redirects)
{
    CURLcode code = curl_easy_setopt(curl, CURLOPT_URL, url);
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_HTTPGET, 1L);
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, follow_redirects ? 1L : 0L);
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_MAXREDIRS, 10L);
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, timeout_milliseconds);
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_ACCEPT_ENCODING, "identity");
    if (code != CURLE_OK) {
        return (int)code;
    }

    code = curl_easy_setopt(curl, CURLOPT_USERAGENT, "Stark-Vendor-Curl/1");
    if (code != CURLE_OK) {
        return (int)code;
    }

    return CURLE_OK;
}

int stark_curl_get_into(
    const char *url,
    char *body_destination,
    size_t body_capacity,
    char *header_destination,
    size_t header_capacity,
    long timeout_milliseconds,
    int follow_redirects,
    long *status_code,
    size_t *body_length,
    size_t *header_length)
{
    if (url == NULL
        || body_destination == NULL
        || header_destination == NULL
        || status_code == NULL
        || body_length == NULL
        || header_length == NULL
        || timeout_milliseconds <= 0) {
        return STARK_CURL_INVALID_ARGUMENT;
    }

    int init_code = stark_curl_ensure_global();
    if (init_code != CURLE_OK) {
        return init_code;
    }

    CURL *curl = curl_easy_init();
    if (curl == NULL) {
        return STARK_CURL_OUT_OF_MEMORY;
    }

    stark_curl_fixed_buffer body = {
        (unsigned char *)body_destination,
        body_capacity,
        0,
        0
    };
    stark_curl_fixed_buffer headers = {
        (unsigned char *)header_destination,
        header_capacity,
        0,
        0
    };

    int prepare_code = stark_curl_prepare_common(curl, url, timeout_milliseconds, follow_redirects);
    if (prepare_code != CURLE_OK) {
        curl_easy_cleanup(curl);
        return prepare_code;
    }

    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, stark_curl_write_fixed_body);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
    curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, stark_curl_write_fixed_headers);
    curl_easy_setopt(curl, CURLOPT_HEADERDATA, &headers);

    CURLcode code = curl_easy_perform(curl);
    long http_status = 0;
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_status);
    curl_easy_cleanup(curl);

    *status_code = http_status;
    *body_length = body.length;
    *header_length = headers.length;

    if (code == CURLE_WRITE_ERROR) {
        if (body.overflow_code != 0) {
            return body.overflow_code;
        }

        if (headers.overflow_code != 0) {
            return headers.overflow_code;
        }
    }

    return (int)code;
}

int stark_curl_get_owned(
    const char *url,
    long timeout_milliseconds,
    int follow_redirects,
    stark_curl_response **response)
{
    if (url == NULL || response == NULL || timeout_milliseconds <= 0) {
        return STARK_CURL_INVALID_ARGUMENT;
    }

    *response = NULL;

    int init_code = stark_curl_ensure_global();
    if (init_code != CURLE_OK) {
        return init_code;
    }

    stark_curl_response *result = (stark_curl_response *)calloc(1, sizeof(stark_curl_response));
    if (result == NULL) {
        return STARK_CURL_OUT_OF_MEMORY;
    }

    CURL *curl = curl_easy_init();
    if (curl == NULL) {
        free(result);
        return STARK_CURL_OUT_OF_MEMORY;
    }

    int prepare_code = stark_curl_prepare_common(curl, url, timeout_milliseconds, follow_redirects);
    if (prepare_code != CURLE_OK) {
        curl_easy_cleanup(curl);
        stark_curl_response_free(result);
        return prepare_code;
    }

    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, stark_curl_write_grow);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &result->body);
    curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, stark_curl_write_grow);
    curl_easy_setopt(curl, CURLOPT_HEADERDATA, &result->headers);

    CURLcode code = curl_easy_perform(curl);
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &result->status_code);
    curl_easy_cleanup(curl);

    if (code != CURLE_OK) {
        stark_curl_response_free(result);
        return (int)code;
    }

    *response = result;
    return CURLE_OK;
}

void stark_curl_response_free(stark_curl_response *response)
{
    if (response == NULL) {
        return;
    }

    free(response->body.data);
    free(response->headers.data);
    free(response);
}

long stark_curl_response_status(const stark_curl_response *response)
{
    return response == NULL ? 0 : response->status_code;
}

const char *stark_curl_response_body(const stark_curl_response *response)
{
    return response == NULL ? NULL : (const char *)response->body.data;
}

size_t stark_curl_response_body_length(const stark_curl_response *response)
{
    return response == NULL ? 0 : response->body.length;
}

const char *stark_curl_response_headers(const stark_curl_response *response)
{
    return response == NULL ? NULL : (const char *)response->headers.data;
}

size_t stark_curl_response_header_length(const stark_curl_response *response)
{
    return response == NULL ? 0 : response->headers.length;
}
