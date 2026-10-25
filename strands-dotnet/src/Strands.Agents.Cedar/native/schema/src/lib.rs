use serde_json::Value;

fn dispatch(input: Value) -> Result<Value, String> {
    use cedar_policy_mcp_schema_generator::{SchemaGenerator, SchemaGeneratorConfig};
    use mcp_tools_sdk::description::ServerDescription;
    if input.get("operation").and_then(Value::as_str) != Some("generateSchema") {
        return Err("Unsupported Cedar schema operation.".to_owned());
    }
    let request = input.get("request").ok_or("request is required")?;
    let stub = request
        .get("stub")
        .and_then(Value::as_str)
        .ok_or("stub must be a string")?;
    let tools = request
        .get("tools")
        .filter(|value| value.is_array())
        .ok_or("tools must be an array")?;
    let description =
        ServerDescription::from_json_str(&tools.to_string()).map_err(|error| error.to_string())?;
    let mut generator = SchemaGenerator::from_cedarschema_str_with_config(
        stub,
        SchemaGeneratorConfig::default().flatten_namespaces(true),
    )
    .map_err(|error| error.to_string())?;
    generator
        .add_actions_from_server_description(&description)
        .map_err(|error| error.to_string())?;
    Ok(serde_json::json!({"schema":generator.get_schema_as_str()}))
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
    const STUB: &str = "namespace Agent { @mcp_principal entity User; @mcp_resource entity Resource; @mcp_context(\"session\") type SessionContext = { hour_utc: Long, call_count: Long }; }";

    #[test]
    fn real_generator_projects_required_inputs_and_session_context() {
        let result = dispatch(json!({"operation":"generateSchema", "request":{
            "stub":STUB, "tools":[{"name":"search", "inputSchema":{"type":"object", "properties":{"query":{"type":"string"}}, "required":["query"]}}]
        }})).unwrap();
        let schema = result["schema"].as_str().unwrap();
        assert!(schema.contains("action \"search\""), "{schema}");
        assert!(schema.contains("query: String"), "{schema}");
        assert!(schema.contains("session:"), "{schema}");
        assert!(schema.contains("call_count: Long"), "{schema}");
    }

    #[test]
    fn malformed_stub_is_a_real_generator_error() {
        assert!(dispatch(
            json!({"operation":"generateSchema", "request":{"stub":"invalid", "tools":[]}})
        )
        .is_err());
    }
}
