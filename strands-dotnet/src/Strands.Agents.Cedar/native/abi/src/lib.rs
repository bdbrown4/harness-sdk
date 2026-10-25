use serde_json::{json, Value};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

/// Executes a JSON-only operation without allowing a panic to cross the C ABI.
///
/// # Safety
/// Non-null input must address `length` readable bytes. Output pointers must be
/// writable and distinct. The returned buffer belongs to this library until
/// released exactly once with `free`, using its original pointer and length.
pub unsafe fn execute(
    input: *const u8,
    length: usize,
    output: *mut *mut u8,
    output_length: *mut usize,
    operation: impl FnOnce(Value) -> Result<Value, String>,
) -> i32 {
    if output.is_null() || output_length.is_null() {
        return 2;
    }
    unsafe {
        *output = ptr::null_mut();
        *output_length = 0;
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        if input.is_null() || length > isize::MAX as usize {
            return Err("Input must address a valid UTF-8 JSON buffer.".to_owned());
        }
        let bytes = unsafe { std::slice::from_raw_parts(input, length) };
        let value = serde_json::from_slice(bytes).map_err(|error| error.to_string())?;
        operation(value)
    }));
    let (status, response) = match result {
        Ok(Ok(value)) => (0, json!({"ok": true, "result": value})),
        Ok(Err(error)) => (1, json!({"ok": false, "error": error})),
        Err(_) => (
            1,
            json!({"ok": false, "error": "Native Cedar operation panicked."}),
        ),
    };
    // A Value built above is always serializable. Allocator exhaustion follows
    // Rust's process-abort behavior rather than attempting to cross the ABI.
    let bytes = response.to_string().into_bytes().into_boxed_slice();
    let count = bytes.len();
    let buffer = Box::into_raw(bytes) as *mut u8;
    unsafe {
        *output = buffer;
        *output_length = count;
    }
    status
}

/// Releases a buffer allocated by `execute` in the same native library.
///
/// # Safety
/// The pointer and length must be the unchanged result of a successful buffer
/// allocation by this library and must not previously have been released.
pub unsafe fn free(buffer: *mut u8, length: usize) {
    if !buffer.is_null() {
        unsafe { drop(Box::from_raw(ptr::slice_from_raw_parts_mut(buffer, length))) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn invoke(
        bytes: &[u8],
        operation: impl FnOnce(Value) -> Result<Value, String>,
    ) -> (i32, Value) {
        let mut buffer = ptr::null_mut();
        let mut length = 0;
        let status = unsafe {
            execute(
                bytes.as_ptr(),
                bytes.len(),
                &mut buffer,
                &mut length,
                operation,
            )
        };
        let value =
            serde_json::from_slice(unsafe { std::slice::from_raw_parts(buffer, length) }).unwrap();
        unsafe { free(buffer, length) };
        (status, value)
    }

    #[test]
    fn buffers_preserve_utf8_and_embedded_nuls() {
        let (status, answer) = invoke(br#"{"value":"snow \u96ea\u0000"}"#, Ok);
        assert_eq!(status, 0);
        assert_eq!(answer["result"]["value"], "snow 雪\0");
    }

    #[test]
    fn errors_and_panics_are_json_protocol_errors() {
        let (status, error) = invoke(b"{}", |_| Err("failure".to_owned()));
        assert_eq!(status, 1);
        assert_eq!(error["error"], "failure");
        let (status, panic) = invoke(b"{}", |_| panic!("secret panic payload"));
        assert_eq!(status, 1);
        assert_eq!(panic["error"], "Native Cedar operation panicked.");
    }

    #[test]
    fn malformed_json_and_utf8_do_not_enter_engine() {
        for bytes in [b"{".as_slice(), &[0xff], b"".as_slice()] {
            let (status, error) = invoke(bytes, |_| panic!("must not dispatch"));
            assert_eq!(status, 1);
            assert_eq!(error["ok"], false);
            assert_ne!(error["error"], "Native Cedar operation panicked.");
        }
    }

    #[test]
    fn null_inputs_and_missing_output_pointers_are_rejected() {
        let mut buffer = ptr::null_mut();
        let mut length = 0;
        assert_eq!(
            unsafe { execute(ptr::null(), 1, &mut buffer, &mut length, Ok) },
            1
        );
        unsafe { free(buffer, length) };
        assert_eq!(
            unsafe { execute(b"{}".as_ptr(), 2, ptr::null_mut(), &mut length, Ok) },
            2
        );
        unsafe { free(ptr::null_mut(), 0) };
    }
}
