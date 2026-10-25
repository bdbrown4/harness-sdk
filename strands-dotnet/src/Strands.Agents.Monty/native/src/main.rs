use serde_json::{Value, json};
use std::{
    io::{self, Read, Write},
    panic::{AssertUnwindSafe, catch_unwind},
    process::ExitCode,
};
use strands_monty_worker::{MAX_FRAME_BYTES, Transport, run};

#[global_allocator]
static ALLOC: monty_alloc::LimitedAllocator = monty_alloc::LimitedAllocator;

struct StdioTransport<R, W> {
    input: R,
    output: W,
}
impl<R: Read, W: Write> Transport for StdioTransport<R, W> {
    fn send(&mut self, event: Value) -> Result<(), String> {
        let data = serde_json::to_vec(&event).map_err(|error| error.to_string())?;
        if data.len() > MAX_FRAME_BYTES {
            return Err("Native event exceeds the frame limit".to_owned());
        }
        self.output
            .write_all(&(data.len() as u32).to_le_bytes())
            .and_then(|_| self.output.write_all(&data))
            .and_then(|_| self.output.flush())
            .map_err(|error| error.to_string())
    }
    fn receive(&mut self) -> Result<Value, String> {
        let mut length = [0_u8; 4];
        self.input
            .read_exact(&mut length)
            .map_err(|error| error.to_string())?;
        let length = u32::from_le_bytes(length) as usize;
        if length == 0 || length > MAX_FRAME_BYTES {
            return Err("Invalid native frame length".to_owned());
        }
        let mut data = vec![0_u8; length];
        self.input
            .read_exact(&mut data)
            .map_err(|error| error.to_string())?;
        serde_json::from_slice(&data).map_err(|error| error.to_string())
    }
}

fn main() -> ExitCode {
    if std::env::args().nth(1).as_deref() == Some("--version") {
        println!("strands_monty_worker ABI1 Monty0.0.23 {}-bit", usize::BITS);
        return ExitCode::SUCCESS;
    }
    let mut transport = StdioTransport {
        input: io::stdin().lock(),
        output: io::stdout().lock(),
    };
    let result = catch_unwind(AssertUnwindSafe(|| {
        transport
            .send(json!({"kind":"ready","abi":1,"engine":"0.0.23","pointer_bits":usize::BITS}))?;
        let request = transport.receive()?;
        run(request, &mut transport)
    }));
    let error = match result {
        Ok(Ok(())) => return ExitCode::SUCCESS,
        Ok(Err(error)) => error,
        Err(_) => "Native Monty worker panicked".to_owned(),
    };
    let _ = transport
        .send(json!({"kind":"worker_error","text":error.chars().take(8192).collect::<String>()}));
    ExitCode::FAILURE
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;
    #[test]
    fn framed_protocol_round_trips_utf8_and_embedded_nul() {
        let value = json!({"code":"print('雪\\0')","functions":[]});
        let mut writer = StdioTransport {
            input: Cursor::new(Vec::<u8>::new()),
            output: Vec::new(),
        };
        writer.send(value.clone()).unwrap();
        let mut reader = StdioTransport {
            input: Cursor::new(writer.output),
            output: Vec::new(),
        };
        assert_eq!(reader.receive().unwrap(), value);
    }
    #[test]
    fn frame_lengths_and_truncation_fail_before_execution() {
        for bytes in [
            vec![0, 0, 0, 0],
            vec![255, 255, 255, 255],
            vec![8, 0, 0, 0, b'{'],
        ] {
            let mut reader = StdioTransport {
                input: Cursor::new(bytes),
                output: Vec::new(),
            };
            assert!(reader.receive().is_err());
        }
    }
}
