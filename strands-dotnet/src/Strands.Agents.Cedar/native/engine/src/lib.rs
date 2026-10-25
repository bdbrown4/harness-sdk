use serde_json::Value;

fn dispatch(input: Value) -> Result<Value, String> {
    let operation = input
        .get("operation")
        .and_then(Value::as_str)
        .ok_or("operation must be a string")?;
    let request = input.get("request").cloned().ok_or("request is required")?;
    let result = match operation {
        "authorize" => cedar_policy::ffi::is_authorized_json(request),
        "parsePolicies" => cedar_policy::ffi::check_parse_policy_set_json(request),
        "parseSchema" => cedar_policy::ffi::check_parse_schema_json(request),
        "parseEntities" => cedar_policy::ffi::check_parse_entities_json(request),
        "validate" => cedar_policy::ffi::validate_json(request),
        "version" => {
            return Ok(
                serde_json::json!({"sdk": cedar_policy::ffi::get_sdk_version(), "language": cedar_policy::ffi::get_lang_version()}),
            )
        }
        _ => return Err(format!("Unsupported Cedar operation: {operation}")),
    };
    result.map_err(|error| error.to_string())
}

/// Executes the version-one JSON protocol. See the sibling ABI safety contract.
#[no_mangle]
pub unsafe extern "C" fn strands_cedar_execute(
    input: *const u8,
    length: usize,
    output: *mut *mut u8,
    output_length: *mut usize,
) -> i32 {
    unsafe { strands_cedar_abi::execute(input, length, output, output_length, dispatch) }
}

/// Frees an unchanged buffer returned by this library, exactly once.
#[no_mangle]
pub unsafe extern "C" fn strands_cedar_free(buffer: *mut u8, length: usize) {
    unsafe { strands_cedar_abi::free(buffer, length) };
}

#[no_mangle]
pub extern "C" fn strands_cedar_abi_version() -> u32 {
    1
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn call(operation: &str, request: Value) -> Value {
        dispatch(json!({"operation": operation, "request": request})).unwrap()
    }

    fn authorize(policies: &str) -> Value {
        call(
            "authorize",
            json!({
                "principal": {"type":"User", "id":"alice"},
                "action": {"type":"Action", "id":"search"},
                "resource": {"type":"Resource", "id":"agent"},
                "context": {"input": {"query":"雪"}, "session":{"call_count":1, "hour_utc":12}},
                "policies": {"staticPolicies":policies}, "entities":[]
            }),
        )
    }

    #[test]
    fn real_engine_permits_context_and_enforces_explicit_forbid() {
        assert_eq!(
            authorize(
                r#"permit(principal == User::"alice", action == Action::"search", resource) when { context.input.query == "雪" && context.session.call_count == 1 };"#
            )["response"]["decision"],
            "allow"
        );
        assert_eq!(
            authorize("permit(principal, action, resource); forbid(principal, action, resource);")
                ["response"]["decision"],
            "deny"
        );
    }

    #[test]
    fn real_parser_returns_diagnostics_for_malformed_policies() {
        let valid = call(
            "parsePolicies",
            json!({"staticPolicies":"permit(principal, action, resource);"}),
        );
        assert_eq!(valid["type"], "success");
        let invalid = call(
            "parsePolicies",
            json!({"staticPolicies":"permit(this is invalid)"}),
        );
        assert_eq!(invalid["type"], "failure");
        assert!(!invalid["errors"].as_array().unwrap().is_empty());
    }

    #[test]
    fn real_validator_rejects_unrecognized_actions() {
        let answer = call(
            "validate",
            json!({
                "schema":"entity User; entity Resource; action search appliesTo { principal: User, resource: Resource, context: {} };",
                "policies":{"staticPolicies":"permit(principal, action == Action::\"unknown\", resource);"}
            }),
        );
        assert_eq!(answer["type"], "success");
        assert!(!answer["validationErrors"].as_array().unwrap().is_empty());
    }

    #[test]
    fn unsupported_operations_fail_without_pretending_to_authorize() {
        assert!(dispatch(json!({"operation":"unknown", "request":{}})).is_err());
    }
}
