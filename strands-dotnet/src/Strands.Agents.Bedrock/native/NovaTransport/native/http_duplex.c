#include <aws/common/byte_buf.h>
#include <aws/common/mutex.h>
#include <aws/common/string.h>
#include <aws/http/connection.h>
#include <aws/http/request_response.h>
#include <aws/io/channel_bootstrap.h>
#include <aws/io/event_loop.h>
#include <aws/io/host_resolver.h>
#include <aws/io/socket.h>
#include <aws/io/stream.h>
#include <aws/io/tls_channel_handler.h>
#include <windows.h>
#include <stdio.h>


void strands_crt_ensure_initialized(void);
typedef void(__cdecl *nova_signal_fn)(void *, int, int);
typedef int(__cdecl *nova_body_fn)(void *, const uint8_t *, uint32_t);
typedef void(__cdecl *nova_write_fn)(void *, int);

struct nova_http {
    struct aws_mutex gate;
    struct aws_event_loop_group *group;
    struct aws_host_resolver *resolver;
    struct aws_client_bootstrap *bootstrap;
    struct aws_tls_ctx *tls;
    struct aws_http_connection *connection;
    struct aws_http_stream *stream;
    struct aws_http_message *request;
    struct aws_string *host;
    struct aws_string *path;
    struct aws_string *headers;
    HANDLE stopped;
    nova_signal_fn signal;
    nova_body_fn body;
    void *user_data;
    uint16_t port;
    bool closing;
    bool resources_released;
    bool completed;
    volatile LONG flags;
    volatile LONG pending_writes;
};

static void s_release_resources(struct nova_http *session);

static void s_group_stopped(void *context) {
    struct nova_http *session = context;
    InterlockedOr(&session->flags, 64);
    SetEvent(session->stopped);
}

static void s_bootstrap_stopped(void *context) {
    struct nova_http *session = context;
    InterlockedOr(&session->flags, 32);
    aws_event_loop_group_release(session->group);
}

static void s_release_resources(struct nova_http *session) {
    aws_mutex_lock(&session->gate);
    bool released = session->resources_released;
    session->resources_released = true;
    aws_mutex_unlock(&session->gate);
    if (released) return;
    if (session->tls) aws_tls_ctx_release(session->tls);
    if (session->resolver) aws_host_resolver_release(session->resolver);
    if (session->bootstrap) aws_client_bootstrap_release(session->bootstrap);
    else if (session->group) aws_event_loop_group_release(session->group);
    else s_group_stopped(session);
}

static void s_notify_complete(struct nova_http *session, int error) {
    aws_mutex_lock(&session->gate);
    bool already_completed = session->completed;
    session->completed = true;
    aws_mutex_unlock(&session->gate);
    if (already_completed) return;
    InterlockedOr(&session->flags, 4);
    session->signal(session->user_data, 3, error);
}

static void s_connection_stopped(struct aws_http_connection *connection, int error, void *context) {
    (void)connection;
    struct nova_http *session = context;
    InterlockedOr(&session->flags, 16);
    s_notify_complete(session, error);
    s_release_resources(session);
}

static void s_stream_destroyed(void *context) {
    struct nova_http *session = context;
    InterlockedOr(&session->flags, 8);
    session->signal(session->user_data, 4, 0);
}

static void s_stream_complete(struct aws_http_stream *stream, int error, void *context) {
    (void)stream;
    struct nova_http *session = context;
    aws_mutex_lock(&session->gate);
    struct aws_http_stream *owned_stream = session->stream;
    struct aws_http_connection *connection = session->connection;
    session->stream = NULL;
    session->connection = NULL;
    aws_mutex_unlock(&session->gate);
    s_notify_complete(session, error);
    if (owned_stream) aws_http_stream_release(owned_stream);
    if (connection) {
        aws_http_connection_close(connection);
        aws_http_connection_release(connection);
    }
}

static int s_headers_done(struct aws_http_stream *stream, enum aws_http_header_block block, void *context) {
    if (block != AWS_HTTP_HEADER_BLOCK_MAIN) return AWS_OP_SUCCESS;
    struct nova_http *session = context;
    int status = 0;
    if (aws_http_stream_get_incoming_response_status(stream, &status)) return AWS_OP_ERR;
    InterlockedOr(&session->flags, 2);
    session->signal(session->user_data, 2, status);
    return AWS_OP_SUCCESS;
}

static int s_body(struct aws_http_stream *stream, const struct aws_byte_cursor *data, void *context) {
    (void)stream;
    struct nova_http *session = context;
    if (data->len > UINT32_MAX || session->body(session->user_data, data->ptr, (uint32_t)data->len))
        return aws_raise_error(AWS_ERROR_INVALID_STATE);
    return AWS_OP_SUCCESS;
}

static void s_setup_failed(struct nova_http *session, int error, struct aws_http_connection *connection) {
    s_notify_complete(session, error);
    if (connection) {
        aws_http_connection_close(connection);
        aws_http_connection_release(connection);
    } else s_release_resources(session);
}

static void s_connected(struct aws_http_connection *connection, int error, void *context) {
    struct nova_http *session = context;
    if (error) { s_setup_failed(session, error, NULL); return; }
    if (aws_http_connection_get_version(connection) != AWS_HTTP_VERSION_2) {
        s_setup_failed(session, AWS_ERROR_HTTP_UNSUPPORTED_PROTOCOL, connection);
        return;
    }
    aws_mutex_lock(&session->gate);
    bool closing = session->closing;
    session->connection = connection;
    aws_mutex_unlock(&session->gate);
    if (closing) { s_stream_complete(NULL, AWS_ERROR_IO_OPERATION_CANCELLED, session); return; }

    struct aws_allocator *allocator = aws_default_allocator();
    session->request = aws_http_message_new_request(allocator);
    if (!session->request) { s_stream_complete(NULL, aws_last_error(), session); return; }
    struct aws_byte_cursor method = aws_byte_cursor_from_c_str("POST");
    struct aws_byte_cursor path = aws_byte_cursor_from_string(session->path);
    if (aws_http_message_set_request_method(session->request, method) || aws_http_message_set_request_path(session->request, path)) {
        s_stream_complete(NULL, aws_last_error(), session);
        return;
    }
    char authority[512];
    int size = snprintf(authority, sizeof(authority), "%s:%u", aws_string_c_str(session->host), (unsigned int)session->port);
    if (size < 0 || size >= (int)sizeof(authority)) { s_stream_complete(NULL, AWS_ERROR_INVALID_ARGUMENT, session); return; }
    struct aws_http_header headers[] = {
        { .name = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("host"), .value = { (size_t)size, (uint8_t *)authority } },
        { .name = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("content-type"), .value = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("application/vnd.amazon.eventstream") }
    };
    int header_error = 0;
    if (session->headers && session->headers->len) {
        const char *cursor = aws_string_c_str(session->headers);
        const char *end = cursor + session->headers->len;
        while (cursor < end) {
            const char *line_end = memchr(cursor, '\n', (size_t)(end - cursor));
            if (!line_end) line_end = end;
            const char *colon = memchr(cursor, ':', (size_t)(line_end - cursor));
            if (!colon || colon == cursor || memchr(cursor, '\r', (size_t)(line_end - cursor))) { header_error = AWS_ERROR_INVALID_ARGUMENT; break; }
            struct aws_http_header header = {
                .name = aws_byte_cursor_from_array(cursor, (size_t)(colon - cursor)),
                .value = aws_byte_cursor_from_array(colon + 1, (size_t)(line_end - colon - 1))
            };
            if (aws_http_message_add_header(session->request, header)) { header_error = aws_last_error(); break; }
            cursor = line_end == end ? end : line_end + 1;
        }
    } else if (aws_http_message_add_header_array(session->request, headers, AWS_ARRAY_SIZE(headers))) header_error = aws_last_error();
    if (header_error) {
        s_stream_complete(NULL, header_error, session);
        return;
    }
    struct aws_http_make_request_options options;
    AWS_ZERO_STRUCT(options);
    options.self_size = sizeof(options);
    options.request = session->request;
    options.user_data = session;
    options.on_response_header_block_done = s_headers_done;
    options.on_response_body = s_body;
    options.on_complete = s_stream_complete;
    options.on_destroy = s_stream_destroyed;
    options.http2_use_manual_data_writes = true;
    struct aws_http_stream *stream = aws_http_connection_make_request(connection, &options);
    if (!stream) { s_stream_complete(NULL, aws_last_error(), session); return; }
    aws_mutex_lock(&session->gate);
    session->stream = stream;
    aws_mutex_unlock(&session->gate);
    if (aws_http_stream_activate(stream)) { s_stream_complete(stream, aws_last_error(), session); return; }
    /* Cancellation of an unactivated CRT stream is a no-op. Recheck after
     * activation so a cancel between publication and activation is not lost. */
    aws_mutex_lock(&session->gate);
    closing = session->closing;
    aws_mutex_unlock(&session->gate);
    InterlockedOr(&session->flags, 1);
    session->signal(session->user_data, 1, 2);
    if (closing) aws_http_stream_cancel(stream, AWS_ERROR_IO_OPERATION_CANCELLED);
}

struct nova_http *strands_crt_http_open(
    const char *host, uint16_t port, const char *path, const char *ca_file, const char *signed_headers,
    nova_signal_fn signal, nova_body_fn body, void *context) {
    if (!host || !path || !port || !signal || !body) return NULL;
    strands_crt_ensure_initialized();
    struct aws_allocator *allocator = aws_default_allocator();
    struct nova_http *session = aws_mem_calloc(allocator, 1, sizeof(struct nova_http));
    if (!session) return NULL;
    aws_mutex_init(&session->gate);
    session->signal = signal;
    session->body = body;
    session->user_data = context;
    session->port = port;
    session->host = aws_string_new_from_c_str(allocator, host);
    session->path = aws_string_new_from_c_str(allocator, path);
    if (signed_headers) session->headers = aws_string_new_from_c_str(allocator, signed_headers);
    session->stopped = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!session->host || !session->path || !session->stopped || (signed_headers && !session->headers)) {
        if (session->host) aws_string_destroy(session->host);
        if (session->path) aws_string_destroy(session->path);
        if (session->headers) aws_string_destroy(session->headers);
        if (session->stopped) CloseHandle(session->stopped);
        aws_mutex_clean_up(&session->gate);
        aws_mem_release(allocator, session);
        return NULL;
    }
    struct aws_shutdown_callback_options shutdown = { .shutdown_callback_fn = s_group_stopped, .shutdown_callback_user_data = session };
    session->group = aws_event_loop_group_new_default(allocator, 1, &shutdown);
    if (!session->group) { s_setup_failed(session, aws_last_error(), NULL); return session; }
    struct aws_host_resolver_default_options resolver;
    AWS_ZERO_STRUCT(resolver);
    resolver.max_entries = 8;
    resolver.el_group = session->group;
    session->resolver = aws_host_resolver_new_default(allocator, &resolver);
    if (!session->resolver) { s_setup_failed(session, aws_last_error(), NULL); return session; }
    struct aws_client_bootstrap_options bootstrap;
    AWS_ZERO_STRUCT(bootstrap);
    bootstrap.event_loop_group = session->group;
    bootstrap.host_resolver = session->resolver;
    bootstrap.on_shutdown_complete = s_bootstrap_stopped;
    bootstrap.user_data = session;
    session->bootstrap = aws_client_bootstrap_new(allocator, &bootstrap);
    if (!session->bootstrap) { s_setup_failed(session, aws_last_error(), NULL); return session; }

    struct aws_tls_ctx_options tls_options;
    aws_tls_ctx_options_init_default_client(&tls_options, allocator);
    tls_options.verify_peer = true;
    aws_tls_ctx_options_set_alpn_list(&tls_options, "h2");
    int tls_error = 0;
    if (ca_file && ca_file[0]) tls_error = aws_tls_ctx_options_override_default_trust_store_from_path(&tls_options, NULL, ca_file);
    if (!tls_error) session->tls = aws_tls_client_ctx_new(allocator, &tls_options);
    aws_tls_ctx_options_clean_up(&tls_options);
    if (!session->tls) { s_setup_failed(session, aws_last_error(), NULL); return session; }
    struct aws_tls_connection_options tls_connection;
    aws_tls_connection_options_init_from_ctx(&tls_connection, session->tls);
    struct aws_byte_cursor host_cursor = aws_byte_cursor_from_string(session->host);
    if (aws_tls_connection_options_set_server_name(&tls_connection, allocator, &host_cursor)) {
        int failure = aws_last_error();
        aws_tls_connection_options_clean_up(&tls_connection);
        s_setup_failed(session, failure, NULL);
        return session;
    }
    struct aws_socket_options socket;
    AWS_ZERO_STRUCT(socket);
    socket.type = AWS_SOCKET_STREAM;
    socket.domain = AWS_SOCKET_IPV4;
    socket.connect_timeout_ms = 10000;
    struct aws_http_client_connection_options options = AWS_HTTP_CLIENT_CONNECTION_OPTIONS_INIT;
    options.allocator = allocator;
    options.bootstrap = session->bootstrap;
    options.host_name = host_cursor;
    options.port = port;
    options.socket_options = &socket;
    options.tls_options = &tls_connection;
    options.user_data = session;
    options.manual_window_management = true;
    options.initial_window_size = 65535;
    options.on_setup = s_connected;
    options.on_shutdown = s_connection_stopped;
    int result = aws_http_client_connect(&options);
    int failure = result ? aws_last_error() : 0;
    aws_tls_connection_options_clean_up(&tls_connection);
    if (failure) s_setup_failed(session, failure, NULL);
    return session;
}

struct nova_write {
    struct nova_http *session;
    struct aws_http_stream *stream;
    struct aws_byte_buf data;
    struct aws_input_stream *input;
    nova_write_fn complete;
    void *context;
};

static void s_write_complete(struct aws_http_stream *stream, int error, void *context) {
    (void)stream;
    struct nova_write *write = context;
    struct aws_http_stream *held = write->stream;
    InterlockedDecrement(&write->session->pending_writes);
    aws_input_stream_release(write->input);
    aws_byte_buf_clean_up_secure(&write->data);
    write->complete(write->context, error);
    aws_mem_release(aws_default_allocator(), write);
    aws_http_stream_release(held);
}

int strands_crt_http_write(struct nova_http *session, const uint8_t *data, uint32_t length, int end_stream, nova_write_fn complete, void *context) {
    if (!session || !complete || length > 24 * 1024 * 1024 || (!data && length)) return AWS_ERROR_INVALID_ARGUMENT;
    aws_mutex_lock(&session->gate);
    struct aws_http_stream *stream = !session->closing && session->stream ? aws_http_stream_acquire(session->stream) : NULL;
    aws_mutex_unlock(&session->gate);
    if (!stream) return AWS_ERROR_INVALID_STATE;
    struct aws_allocator *allocator = aws_default_allocator();
    struct nova_write *write = aws_mem_calloc(allocator, 1, sizeof(struct nova_write));
    if (!write) { aws_http_stream_release(stream); return AWS_ERROR_OOM; }
    struct aws_byte_cursor cursor = aws_byte_cursor_from_array(data, length);
    int error = 0;
    if (aws_byte_buf_init_copy_from_cursor(&write->data, allocator, cursor)) error = aws_last_error();
    else {
        cursor = aws_byte_cursor_from_buf(&write->data);
        write->input = aws_input_stream_new_from_cursor(allocator, &cursor);
        if (!write->input) error = aws_last_error();
    }
    write->session = session;
    write->stream = stream;
    write->complete = complete;
    write->context = context;
    if (!error) {
        struct aws_http2_stream_write_data_options options;
        AWS_ZERO_STRUCT(options);
        options.data = write->input;
        options.end_stream = end_stream != 0;
        options.on_complete = s_write_complete;
        options.user_data = write;
        InterlockedIncrement(&session->pending_writes);
        if (!aws_http2_stream_write_data(stream, &options)) return 0;
        error = aws_last_error();
        InterlockedDecrement(&session->pending_writes);
    }
    if (write->input) aws_input_stream_release(write->input);
    aws_byte_buf_clean_up_secure(&write->data);
    aws_mem_release(allocator, write);
    aws_http_stream_release(stream);
    return error;
}

void strands_crt_http_update_window(struct nova_http *session, uint32_t amount) {
    aws_mutex_lock(&session->gate);
    struct aws_http_stream *stream = session->stream ? aws_http_stream_acquire(session->stream) : NULL;
    aws_mutex_unlock(&session->gate);
    if (stream) { aws_http_stream_update_window(stream, amount); aws_http_stream_release(stream); }
}

void strands_crt_http_cancel(struct nova_http *session) {
    aws_mutex_lock(&session->gate);
    session->closing = true;
    struct aws_http_stream *stream = session->stream ? aws_http_stream_acquire(session->stream) : NULL;
    aws_mutex_unlock(&session->gate);
    if (stream) { aws_http_stream_cancel(stream, AWS_ERROR_IO_OPERATION_CANCELLED); aws_http_stream_release(stream); }
}

int strands_crt_http_pending_writes(struct nova_http *session) {
    return InterlockedCompareExchange(&session->pending_writes, 0, 0);
}

int strands_crt_http_join_and_destroy(struct nova_http *session, int *flags) {
    if (!session || !flags) return AWS_ERROR_INVALID_ARGUMENT;
    DWORD result = WaitForSingleObject(session->stopped, INFINITE);
    if (result != WAIT_OBJECT_0) return AWS_ERROR_UNKNOWN;
    *flags = (int)InterlockedCompareExchange(&session->flags, 0, 0);
    if (session->request) aws_http_message_release(session->request);
    CloseHandle(session->stopped);
    aws_string_destroy(session->host);
    aws_string_destroy(session->path);
    if (session->headers) aws_string_destroy_secure(session->headers);
    aws_mutex_clean_up(&session->gate);
    aws_mem_release(aws_default_allocator(), session);
    return 0;
}
