#include <aws/auth/auth.h>
#include <aws/auth/credentials.h>
#include <aws/auth/signable.h>
#include <aws/auth/signing.h>
#include <aws/auth/signing_config.h>
#include <aws/auth/signing_result.h>
#include <aws/common/condition_variable.h>
#include <aws/common/date_time.h>
#include <aws/common/encoding.h>
#include <aws/common/mutex.h>
#include <aws/common/string.h>
#include <aws/io/stream.h>
#include <aws/http/request_response.h>
#include <windows.h>



static INIT_ONCE s_init_once = INIT_ONCE_STATIC_INIT;

int strands_crt_abi_version(void) { return 2; }

static BOOL CALLBACK s_initialize(PINIT_ONCE once, PVOID parameter, PVOID *context) {
    (void)once;
    (void)parameter;
    (void)context;
    aws_auth_library_init(aws_default_allocator());
    return TRUE;
}

void strands_crt_ensure_initialized(void) {
    InitOnceExecuteOnce(&s_init_once, s_initialize, NULL, NULL);
}

struct signing_completion {
    struct aws_mutex mutex;
    struct aws_condition_variable condition;
    bool done;
    int error;
    uint8_t signature[32];
    struct aws_http_message *request;
};

static bool s_signing_done(void *context) {
    return ((struct signing_completion *)context)->done;
}

static void s_event_signed(struct aws_signing_result *result, int error_code, void *context) {
    struct signing_completion *completion = context;
    aws_mutex_lock(&completion->mutex);
    completion->error = error_code;
    if (!error_code && completion->request && aws_apply_signing_result_to_http_request(completion->request, aws_default_allocator(), result))
        completion->error = aws_last_error();
    if (!completion->error) {
        struct aws_string *hex_signature = NULL;
        if (aws_signing_result_get_property(result, g_aws_signature_property_name, &hex_signature) || !hex_signature) {
            completion->error = aws_last_error();
        } else {
            struct aws_byte_cursor hex = aws_byte_cursor_from_string(hex_signature);
            struct aws_byte_buf output = aws_byte_buf_from_empty_array(completion->signature, sizeof(completion->signature));
            if (aws_hex_decode(&hex, &output)) completion->error = aws_last_error();
            else if (output.len != sizeof(completion->signature)) completion->error = AWS_ERROR_INVALID_ARGUMENT;
        }
    }
    completion->done = true;
    aws_condition_variable_notify_all(&completion->condition);
    aws_mutex_unlock(&completion->mutex);
}

int strands_crt_sign_http_message(struct aws_http_message *request,
    const char *access, const char *secret, const char *token, const char *region,
    int64_t epoch_millis, uint8_t *signature) {
    if (!request || !access || !secret || !region || !signature || epoch_millis < 0) return AWS_ERROR_INVALID_ARGUMENT;
    strands_crt_ensure_initialized();
    struct aws_allocator *allocator = aws_default_allocator();
    struct aws_credentials *credentials = aws_credentials_new(allocator, aws_byte_cursor_from_c_str(access),
        aws_byte_cursor_from_c_str(secret), aws_byte_cursor_from_c_str(token ? token : ""), UINT64_MAX);
    if (!credentials) return aws_last_error();
    struct aws_signable *signable = aws_signable_new_http_request(allocator, request);
    if (!signable) { aws_credentials_release(credentials); return aws_last_error(); }
    struct signing_completion completion;
    AWS_ZERO_STRUCT(completion);
    completion.request = request;
    aws_mutex_init(&completion.mutex);
    aws_condition_variable_init(&completion.condition);
    struct aws_signing_config_aws config;
    AWS_ZERO_STRUCT(config);
    config.config_type = AWS_SIGNING_CONFIG_AWS;
    config.algorithm = AWS_SIGNING_ALGORITHM_V4;
    config.signature_type = AWS_ST_HTTP_REQUEST_HEADERS;
    config.region = aws_byte_cursor_from_c_str(region);
    config.service = aws_byte_cursor_from_c_str("bedrock");
    config.credentials = credentials;
    config.flags.use_double_uri_encode = true;
    config.flags.should_normalize_uri_path = true;
    config.signed_body_value = g_aws_signed_body_value_streaming_aws4_hmac_sha256_events;
    config.signed_body_header = AWS_SBHT_X_AMZ_CONTENT_SHA256;
    aws_date_time_init_epoch_millis(&config.date, (uint64_t)epoch_millis);
    int error = 0;
    if (aws_sign_request_aws(allocator, signable, (const struct aws_signing_config_base *)&config, s_event_signed, &completion)) error = aws_last_error();
    else {
        aws_mutex_lock(&completion.mutex);
        aws_condition_variable_wait_pred(&completion.condition, &completion.mutex, s_signing_done, &completion);
        error = completion.error;
        if (!error) memcpy(signature, completion.signature, sizeof(completion.signature));
        aws_mutex_unlock(&completion.mutex);
    }
    aws_condition_variable_clean_up(&completion.condition);
    aws_mutex_clean_up(&completion.mutex);
    aws_signable_destroy(signable);
    aws_credentials_release(credentials);
    return error;
}

int strands_crt_sign_request(const char *access, const char *secret, const char *token, const char *region,
    const char *host, const char *path, int64_t epoch_millis, uint8_t *headers_out, uint32_t capacity, uint32_t *written,
    uint8_t *signature) {
    if (!host || !path || !headers_out || !written) return AWS_ERROR_INVALID_ARGUMENT;
    strands_crt_ensure_initialized();
    struct aws_http_message *request = aws_http_message_new_request(aws_default_allocator());
    if (!request) return aws_last_error();
    struct aws_http_header headers[] = {
        { .name = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("host"), .value = aws_byte_cursor_from_c_str(host) },
        { .name = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("content-type"), .value = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("application/vnd.amazon.eventstream") },
        { .name = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("accept"), .value = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("application/vnd.amazon.eventstream") },
        { .name = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("x-amz-api-version"), .value = AWS_BYTE_CUR_INIT_FROM_STRING_LITERAL("2023-09-30") }
    };
    int error = 0;
    if (aws_http_message_set_request_method(request, aws_byte_cursor_from_c_str("POST")) ||
        aws_http_message_set_request_path(request, aws_byte_cursor_from_c_str(path)) ||
        aws_http_message_add_header_array(request, headers, AWS_ARRAY_SIZE(headers))) error = aws_last_error();
    if (!error) error = strands_crt_sign_http_message(request, access, secret, token, region, epoch_millis, signature);
    size_t offset = 0;
    for (size_t i = 0; !error && i < aws_http_message_get_header_count(request); ++i) {
        struct aws_http_header header;
        if (aws_http_message_get_header(request, &header, i)) { error = aws_last_error(); break; }
        if (header.name.len + header.value.len + 2 > capacity - offset) { error = AWS_ERROR_SHORT_BUFFER; break; }
        memcpy(headers_out + offset, header.name.ptr, header.name.len); offset += header.name.len;
        headers_out[offset++] = ':';
        memcpy(headers_out + offset, header.value.ptr, header.value.len); offset += header.value.len;
        headers_out[offset++] = '\n';
    }
    if (!error) *written = (uint32_t)offset;
    aws_http_message_release(request);
    return error;
}

const char *strands_crt_error_name(int error) {
    return aws_error_name(error);
}

int strands_crt_sign_event(
    const char *secret,
    const char *region,
    const char *previous_signature,
    int64_t epoch_millis,
    const uint8_t *event_bytes,
    size_t event_length,
    uint8_t *out_signature) {

    if (!secret || !region || !previous_signature || !event_bytes || !out_signature ||
        strlen(previous_signature) != 64 || event_length > 24 * 1024 * 1024 || epoch_millis < 0) return AWS_ERROR_INVALID_ARGUMENT;
    strands_crt_ensure_initialized();
    struct aws_allocator *allocator = aws_default_allocator();
    struct aws_byte_cursor access = aws_byte_cursor_from_c_str("event-signing");
    struct aws_byte_cursor secret_cursor = aws_byte_cursor_from_c_str(secret);
    struct aws_byte_cursor token = aws_byte_cursor_from_c_str("");
    struct aws_credentials *credentials = aws_credentials_new(allocator, access, secret_cursor, token, UINT64_MAX);
    if (!credentials) return aws_last_error();
    struct aws_byte_cursor body = aws_byte_cursor_from_array(event_bytes, event_length);
    struct aws_input_stream *stream = aws_input_stream_new_from_cursor(allocator, &body);
    if (!stream) { aws_credentials_release(credentials); return aws_last_error(); }
    struct aws_signable *signable = aws_signable_new_chunk(allocator, stream, aws_byte_cursor_from_c_str(previous_signature));
    if (!signable) { aws_input_stream_release(stream); aws_credentials_release(credentials); return aws_last_error(); }

    struct signing_completion completion;
    AWS_ZERO_STRUCT(completion);
    aws_mutex_init(&completion.mutex);
    aws_condition_variable_init(&completion.condition);
    struct aws_signing_config_aws config;
    AWS_ZERO_STRUCT(config);
    config.config_type = AWS_SIGNING_CONFIG_AWS;
    config.algorithm = AWS_SIGNING_ALGORITHM_V4;
    config.signature_type = AWS_ST_HTTP_REQUEST_EVENT;
    config.region = aws_byte_cursor_from_c_str(region);
    config.service = aws_byte_cursor_from_c_str("bedrock");
    config.credentials = credentials;
    aws_date_time_init_epoch_millis(&config.date, (uint64_t)epoch_millis);

    int error = 0;
    if (aws_sign_request_aws(allocator, signable, (const struct aws_signing_config_base *)&config, s_event_signed, &completion)) error = aws_last_error();
    else {
        aws_mutex_lock(&completion.mutex);
        aws_condition_variable_wait_pred(&completion.condition, &completion.mutex, s_signing_done, &completion);
        error = completion.error;
        if (!error) memcpy(out_signature, completion.signature, sizeof(completion.signature));
        aws_mutex_unlock(&completion.mutex);
    }
    aws_condition_variable_clean_up(&completion.condition);
    aws_mutex_clean_up(&completion.mutex);
    aws_signable_destroy(signable);
    aws_input_stream_release(stream);
    aws_credentials_release(credentials);
    return error;
}
