#include <openssl/err.h>
#include <openssl/ssl.h>

#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct StarkHttpsClient {
    SSL_CTX *ctx;
    BIO *bio;
    SSL *ssl;
} StarkHttpsClient;

static void stark_https_cleanup(StarkHttpsClient *client)
{
    if (client == NULL) {
        return;
    }

    if (client->bio != NULL) {
        BIO_free_all(client->bio);
        client->bio = NULL;
        client->ssl = NULL;
    }

    if (client->ctx != NULL) {
        SSL_CTX_free(client->ctx);
        client->ctx = NULL;
    }

    free(client);
}

void *stark_https_client_connect(const char *host, int32_t port)
{
    if (host == NULL || port <= 0 || port > 65535) {
        return NULL;
    }

    StarkHttpsClient *client = (StarkHttpsClient *)calloc(1, sizeof(StarkHttpsClient));
    if (client == NULL) {
        return NULL;
    }

    client->ctx = SSL_CTX_new(TLS_client_method());
    if (client->ctx == NULL) {
        stark_https_cleanup(client);
        return NULL;
    }

    SSL_CTX_set_verify(client->ctx, SSL_VERIFY_PEER, NULL);
    if (SSL_CTX_set_default_verify_paths(client->ctx) != 1) {
        stark_https_cleanup(client);
        return NULL;
    }

    char target[512];
    int targetLength = snprintf(target, sizeof(target), "%s:%d", host, port);
    if (targetLength <= 0 || (size_t)targetLength >= sizeof(target)) {
        stark_https_cleanup(client);
        return NULL;
    }

    client->bio = BIO_new_ssl_connect(client->ctx);
    if (client->bio == NULL) {
        stark_https_cleanup(client);
        return NULL;
    }

    BIO_get_ssl(client->bio, &client->ssl);
    if (client->ssl == NULL) {
        stark_https_cleanup(client);
        return NULL;
    }
    SSL_set_mode(client->ssl, SSL_MODE_AUTO_RETRY);

    if (SSL_set_tlsext_host_name(client->ssl, host) != 1) {
        stark_https_cleanup(client);
        return NULL;
    }

    if (SSL_set1_host(client->ssl, host) != 1) {
        stark_https_cleanup(client);
        return NULL;
    }

    BIO_set_conn_hostname(client->bio, target);
    if (BIO_do_connect(client->bio) != 1) {
        stark_https_cleanup(client);
        return NULL;
    }

    if (BIO_do_handshake(client->bio) != 1) {
        stark_https_cleanup(client);
        return NULL;
    }

    if (SSL_get_verify_result(client->ssl) != X509_V_OK) {
        stark_https_cleanup(client);
        return NULL;
    }

    return client;
}

int64_t stark_https_client_write(void *handle, const char *source)
{
    StarkHttpsClient *client = (StarkHttpsClient *)handle;
    if (client == NULL || client->bio == NULL || source == NULL) {
        return -1;
    }

    size_t length = strlen(source);
    size_t written = 0;
    while (written < length) {
        int count = BIO_write(client->bio, source + written, (int)(length - written));
        if (count <= 0) {
            if (BIO_should_retry(client->bio)) {
                continue;
            }

            return -1;
        }

        written += (size_t)count;
    }

    return (int64_t)written;
}

int64_t stark_https_client_read(void *handle, char *destination, int64_t capacity)
{
    StarkHttpsClient *client = (StarkHttpsClient *)handle;
    if (client == NULL || client->bio == NULL || destination == NULL || capacity < 0) {
        return -1;
    }

    if (capacity == 0) {
        return 0;
    }

    if (capacity > INT32_MAX) {
        capacity = INT32_MAX;
    }

    while (1) {
        int count = BIO_read(client->bio, destination, (int)capacity);
        if (count > 0) {
            return (int64_t)count;
        }

        if (count == 0) {
            return 0;
        }

        if (!BIO_should_retry(client->bio)) {
            return -1;
        }
    }
}

void stark_https_client_close(void *handle)
{
    stark_https_cleanup((StarkHttpsClient *)handle);
}
