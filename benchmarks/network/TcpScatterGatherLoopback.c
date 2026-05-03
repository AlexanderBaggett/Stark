#include <stdint.h>
#include <stddef.h>
#include <string.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#pragma comment(lib, "Ws2_32.lib")
typedef SOCKET socket_handle;
#else
#include <arpa/inet.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <sys/uio.h>
#include <unistd.h>
typedef int socket_handle;
#endif

enum {
    FIRST_CHUNK_BYTES = 1536,
    SECOND_CHUNK_BYTES = 2560,
    CHUNK_BYTES = FIRST_CHUNK_BYTES + SECOND_CHUNK_BYTES,
    ITERATIONS = 256,
    EXPECTED_BYTES = CHUNK_BYTES * ITERATIONS
};

static uint16_t loopback_port(int attempt) {
#ifdef _WIN32
    long pid = (long)GetCurrentProcessId();
#else
    long pid = (long)getpid();
#endif
    if (pid < 0) {
        pid = 0;
    }

    return (uint16_t)(41000 + (pid % 20000) + attempt);
}

static int is_invalid_socket(socket_handle handle) {
#ifdef _WIN32
    return handle == INVALID_SOCKET;
#else
    return handle < 0;
#endif
}

static void close_socket(socket_handle handle) {
    if (is_invalid_socket(handle)) {
        return;
    }

#ifdef _WIN32
    closesocket(handle);
#else
    close(handle);
#endif
}

static socket_handle make_listener(uint16_t port) {
    socket_handle listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (is_invalid_socket(listener)) {
        return (socket_handle)-1;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);

    if (bind(listener, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        close_socket(listener);
        return (socket_handle)-1;
    }

    if (listen(listener, 128) != 0) {
        close_socket(listener);
        return (socket_handle)-1;
    }

    return listener;
}

static socket_handle connect_client(uint16_t port) {
    socket_handle client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (is_invalid_socket(client)) {
        return (socket_handle)-1;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);

    if (connect(client, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        close_socket(client);
        return (socket_handle)-1;
    }

    return client;
}

static void consume(size_t *first_done, size_t first_len, size_t *second_done, size_t amount) {
    if (*first_done < first_len) {
        size_t remaining_first = first_len - *first_done;
        if (amount <= remaining_first) {
            *first_done += amount;
            return;
        }

        *first_done = first_len;
        amount -= remaining_first;
    }

    *second_done += amount;
}

static int write_all_vectored(socket_handle fd, const unsigned char *first, size_t first_len, const unsigned char *second, size_t second_len) {
    size_t first_written = 0;
    size_t second_written = 0;
    while (first_written < first_len || second_written < second_len) {
#ifdef _WIN32
        WSABUF buffers[2];
        DWORD buffer_count = 0;
        if (first_written < first_len) {
            buffers[buffer_count].buf = (CHAR *)(first + first_written);
            buffers[buffer_count].len = (ULONG)(first_len - first_written);
            buffer_count++;
        }

        if (second_written < second_len) {
            buffers[buffer_count].buf = (CHAR *)(second + second_written);
            buffers[buffer_count].len = (ULONG)(second_len - second_written);
            buffer_count++;
        }

        DWORD bytes_written = 0;
        if (WSASend(fd, buffers, buffer_count, &bytes_written, 0, NULL, NULL) != 0 || bytes_written == 0) {
            return 0;
        }

        consume(&first_written, first_len, &second_written, (size_t)bytes_written);
#else
        struct iovec buffers[2];
        int buffer_count = 0;
        if (first_written < first_len) {
            buffers[buffer_count].iov_base = (void *)(first + first_written);
            buffers[buffer_count].iov_len = first_len - first_written;
            buffer_count++;
        }

        if (second_written < second_len) {
            buffers[buffer_count].iov_base = (void *)(second + second_written);
            buffers[buffer_count].iov_len = second_len - second_written;
            buffer_count++;
        }

        ssize_t bytes_written = writev(fd, buffers, buffer_count);
        if (bytes_written <= 0) {
            return 0;
        }

        consume(&first_written, first_len, &second_written, (size_t)bytes_written);
#endif
    }

    return 1;
}

static int read_exact_vectored(socket_handle fd, unsigned char *first, size_t first_len, unsigned char *second, size_t second_len) {
    size_t first_read = 0;
    size_t second_read = 0;
    while (first_read < first_len || second_read < second_len) {
#ifdef _WIN32
        WSABUF buffers[2];
        DWORD buffer_count = 0;
        if (first_read < first_len) {
            buffers[buffer_count].buf = (CHAR *)(first + first_read);
            buffers[buffer_count].len = (ULONG)(first_len - first_read);
            buffer_count++;
        }

        if (second_read < second_len) {
            buffers[buffer_count].buf = (CHAR *)(second + second_read);
            buffers[buffer_count].len = (ULONG)(second_len - second_read);
            buffer_count++;
        }

        DWORD bytes_read = 0;
        DWORD flags = 0;
        if (WSARecv(fd, buffers, buffer_count, &bytes_read, &flags, NULL, NULL) != 0 || bytes_read == 0) {
            return 0;
        }

        consume(&first_read, first_len, &second_read, (size_t)bytes_read);
#else
        struct iovec buffers[2];
        int buffer_count = 0;
        if (first_read < first_len) {
            buffers[buffer_count].iov_base = first + first_read;
            buffers[buffer_count].iov_len = first_len - first_read;
            buffer_count++;
        }

        if (second_read < second_len) {
            buffers[buffer_count].iov_base = second + second_read;
            buffers[buffer_count].iov_len = second_len - second_read;
            buffer_count++;
        }

        ssize_t bytes_read = readv(fd, buffers, buffer_count);
        if (bytes_read <= 0) {
            return 0;
        }

        consume(&first_read, first_len, &second_read, (size_t)bytes_read);
#endif
    }

    return 1;
}

int main(void) {
#ifdef _WIN32
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
        return 9;
    }
#endif

    unsigned char write_first[FIRST_CHUNK_BYTES];
    unsigned char write_second[SECOND_CHUNK_BYTES];
    unsigned char read_first[FIRST_CHUNK_BYTES];
    unsigned char read_second[SECOND_CHUNK_BYTES];
    memset(write_first, 0, sizeof(write_first));
    memset(write_second, 0, sizeof(write_second));
    memset(read_first, 0, sizeof(read_first));
    memset(read_second, 0, sizeof(read_second));

    socket_handle listener = (socket_handle)-1;
    uint16_t port = 0;
    for (int attempt = 0; attempt < 64; attempt++) {
        port = loopback_port(attempt);
        listener = make_listener(port);
        if (!is_invalid_socket(listener)) {
            break;
        }
    }

    if (is_invalid_socket(listener)) {
        return 1;
    }

    socket_handle client = connect_client(port);
    if (is_invalid_socket(client)) {
        close_socket(listener);
        return 2;
    }

    socket_handle server = accept(listener, NULL, NULL);
    close_socket(listener);
    if (is_invalid_socket(server)) {
        close_socket(client);
        return 3;
    }

    size_t total_read = 0;
    for (int iteration = 0; iteration < ITERATIONS; iteration++) {
        if (!write_all_vectored(client, write_first, FIRST_CHUNK_BYTES, write_second, SECOND_CHUNK_BYTES)) {
            close_socket(server);
            close_socket(client);
            return 4;
        }

        if (!read_exact_vectored(server, read_first, FIRST_CHUNK_BYTES, read_second, SECOND_CHUNK_BYTES)) {
            close_socket(server);
            close_socket(client);
            return 5;
        }

        total_read += CHUNK_BYTES;
    }

#ifdef _WIN32
    shutdown(client, SD_SEND);
#else
    shutdown(client, SHUT_WR);
#endif
    close_socket(client);
    close_socket(server);

#ifdef _WIN32
    WSACleanup();
#endif

    if (total_read != EXPECTED_BYTES) {
        return 6;
    }

    return 0;
}
