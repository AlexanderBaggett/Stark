// stark-bench: skip-c-windows
#include <arpa/inet.h>
#include <stdint.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

enum {
    CHUNK_BYTES = 4096,
    ITERATIONS = 256,
    EXPECTED_BYTES = CHUNK_BYTES * ITERATIONS
};

static uint16_t loopback_port(int attempt) {
    long pid = (long)getpid();
    if (pid < 0) {
        pid = 0;
    }

    return (uint16_t)(41000 + (pid % 20000) + attempt);
}

static int make_listener(uint16_t port) {
    int listener = socket(AF_INET, SOCK_STREAM, 0);
    if (listener < 0) {
        return -1;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);

    if (bind(listener, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        close(listener);
        return -1;
    }

    if (listen(listener, 128) != 0) {
        close(listener);
        return -1;
    }

    return listener;
}

static int connect_client(uint16_t port) {
    int client = socket(AF_INET, SOCK_STREAM, 0);
    if (client < 0) {
        return -1;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);

    if (connect(client, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        close(client);
        return -1;
    }

    return client;
}

static int write_all(int fd, const unsigned char *buffer, size_t length) {
    size_t written = 0;
    while (written < length) {
        ssize_t count = write(fd, buffer + written, length - written);
        if (count <= 0) {
            return 0;
        }

        written += (size_t)count;
    }

    return 1;
}

static int read_exact(int fd, unsigned char *buffer, size_t length) {
    size_t read_count = 0;
    while (read_count < length) {
        ssize_t count = read(fd, buffer + read_count, length - read_count);
        if (count <= 0) {
            return 0;
        }

        read_count += (size_t)count;
    }

    return 1;
}

int main(void) {
    unsigned char write_buffer[CHUNK_BYTES];
    unsigned char read_buffer[CHUNK_BYTES];
    memset(write_buffer, 0, sizeof(write_buffer));
    memset(read_buffer, 0, sizeof(read_buffer));

    int listener = -1;
    uint16_t port = 0;
    for (int attempt = 0; attempt < 64; attempt++) {
        port = loopback_port(attempt);
        listener = make_listener(port);
        if (listener >= 0) {
            break;
        }
    }

    if (listener < 0) {
        return 1;
    }

    int client = connect_client(port);
    if (client < 0) {
        close(listener);
        return 2;
    }

    int server = accept(listener, NULL, NULL);
    close(listener);
    if (server < 0) {
        close(client);
        return 3;
    }

    size_t total_read = 0;
    for (int iteration = 0; iteration < ITERATIONS; iteration++) {
        if (!write_all(client, write_buffer, CHUNK_BYTES)) {
            close(server);
            close(client);
            return 4;
        }

        if (!read_exact(server, read_buffer, CHUNK_BYTES)) {
            close(server);
            close(client);
            return 5;
        }

        total_read += CHUNK_BYTES;
    }

    shutdown(client, SHUT_WR);
    close(client);
    close(server);

    if (total_read != EXPECTED_BYTES) {
        return 6;
    }

    return 0;
}
