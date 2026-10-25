//! A bounded transport around the pinned genuine Monty VM. The VM executes in
//! a dedicated worker because its official allocator may exit on hard limits.

use monty::{MontyRun, RunProgress};
use monty_types::{
    CompileOptions, ExcType, ExtFunctionResult, MontyException, MontyObject, NameLookupResult,
    PrintWriter, PrintWriterCallback, ResourceLimits, ResourceTracker,
};
use serde_json::{Map, Value, json};
use std::{borrow::Cow, collections::HashSet, time::Duration};

#[cfg(test)]
#[global_allocator]
static ALLOC: monty_alloc::LimitedAllocator = monty_alloc::LimitedAllocator;

pub const MAX_FRAME_BYTES: usize = 32 * 1024 * 1024;
const OUTPUT_UTF16_LIMIT: usize = 200_002;

pub trait Transport {
    fn send(&mut self, event: Value) -> Result<(), String>;
    fn receive(&mut self) -> Result<Value, String>;
}

pub fn run(request: Value, transport: &mut impl Transport) -> Result<(), String> {
    let code = request
        .get("code")
        .and_then(Value::as_str)
        .ok_or("code must be a string")?;
    let functions = request
        .get("functions")
        .and_then(Value::as_array)
        .ok_or("functions must be an array")?
        .iter()
        .map(|v| {
            v.as_str()
                .map(str::to_owned)
                .ok_or("function names must be strings")
        })
        .collect::<Result<HashSet<_>, _>>()?;
    let duration = positive_limit(&request, "max_duration_ms", 60_000)?;
    let memory = usize::try_from(positive_limit(
        &request,
        "max_memory_bytes",
        256 * 1024 * 1024,
    )?)
    .map_err(|_| "memory budget exceeds the native architecture")?;
    let suspensions = positive_limit(&request, "max_suspensions", 1_000)?;
    let recursion = usize::try_from(positive_limit(&request, "max_recursion_depth", 1_000)?)
        .map_err(|_| "recursion budget exceeds the native architecture")?;
    monty_alloc::set_limit(Some(memory), false).map_err(str::to_owned)?;
    let tracker = ResourceTracker::new(
        ResourceLimits::default()
            .max_duration(Duration::from_millis(duration))
            .max_memory(memory)
            .max_recursion_depth(recursion),
    );
    let runner = match MontyRun::new(
        code.to_owned(),
        "<programmatic_tool_caller>",
        vec![],
        CompileOptions::default(),
    ) {
        Ok(runner) => runner,
        Err(error) => return report_error(transport, error, true),
    };
    let mut printed = 0;
    let mut progress = step(transport, &mut printed, |print| {
        runner.start(vec![], tracker, print)
    });
    let mut count = 0;
    loop {
        let value = match progress {
            Ok(value) => value,
            Err(error) => return report_error(transport, error, false),
        };
        if let RunProgress::Complete(_) = value {
            return transport.send(json!({"kind":"complete"}));
        }
        count += 1;
        if count > suspensions {
            return transport.send(json!({"kind":"error","syntax":false,"text":"RuntimeError: maximum suspensions exceeded"}));
        }
        progress = match value {
            RunProgress::NameLookup(lookup) => {
                let answer = if lookup.object_id().is_none() && functions.contains(&lookup.name) {
                    NameLookupResult::Value(MontyObject::Function {
                        name: lookup.name.clone(),
                        docstring: None,
                    })
                } else {
                    NameLookupResult::Undefined
                };
                step(transport, &mut printed, |print| {
                    lookup.resume(answer, print)
                })
            }
            RunProgress::FunctionCall(call) => {
                // Monty can emit a bare external call without first yielding NameLookup.
                // Missing tools therefore need the same catchable NameError on both paths.
                if call.object_id.is_some() || !functions.contains(&call.function_name) {
                    let error = MontyException::new(
                        ExcType::NameError,
                        Some(format!("name '{}' is not defined", call.function_name)),
                    );
                    progress = step(transport, &mut printed, |print| call.resume(error, print));
                    continue;
                }
                let arguments = (|| {
                    let args = call
                        .args
                        .iter()
                        .map(|v| to_json(v, 0))
                        .collect::<Result<Vec<_>, _>>()?;
                    let mut kwargs = Map::new();
                    for (key, value) in &call.kwargs {
                        let MontyObject::String(key) = key else {
                            return Err("Keyword names must be strings".to_owned());
                        };
                        if kwargs.insert(key.clone(), to_json(value, 0)?).is_some() {
                            return Err("Duplicate keyword argument".to_owned());
                        }
                    }
                    Ok(
                        json!({"kind":"call","id":call.call_id,"name":call.function_name,"args":args,"kwargs":kwargs}),
                    )
                })();
                match arguments {
                    Ok(event) => {
                        transport.send(event)?;
                        step(transport, &mut printed, |print| call.resume_pending(print))
                    }
                    Err(error) => step(transport, &mut printed, |print| {
                        call.resume(MontyException::new(ExcType::TypeError, Some(error)), print)
                    }),
                }
            }
            RunProgress::ResolveFutures(futures) => {
                transport.send(json!({"kind":"resolve","ids":futures.pending_call_ids()}))?;
                let reply = transport.receive()?;
                let entries = reply
                    .get("results")
                    .and_then(Value::as_array)
                    .ok_or("results must be an array")?;
                if entries.is_empty() {
                    return Err("At least one pending result must be resolved".to_owned());
                }
                let mut ids = HashSet::new();
                let mut results = Vec::with_capacity(entries.len());
                for entry in entries {
                    let id = entry
                        .get("id")
                        .and_then(Value::as_u64)
                        .and_then(|id| u32::try_from(id).ok())
                        .ok_or("Invalid call identifier")?;
                    if !ids.insert(id) || !futures.pending_call_ids().contains(&id) {
                        return Err("Unknown or repeated call identifier".to_owned());
                    }
                    let result = if let Some(error) = entry.get("error") {
                        let kind = if entry.get("type").and_then(Value::as_str) == Some("TypeError")
                        {
                            ExcType::TypeError
                        } else {
                            ExcType::RuntimeError
                        };
                        ExtFunctionResult::Error(MontyException::new(
                            kind,
                            Some(error.as_str().ok_or("error must be a string")?.to_owned()),
                        ))
                    } else {
                        ExtFunctionResult::Return(from_json(
                            entry.get("value").ok_or("value is required")?,
                            0,
                        )?)
                    };
                    results.push((id, result));
                }
                step(transport, &mut printed, |print| {
                    futures.resume(results, print)
                })
            }
            RunProgress::OsCall(call) => step(transport, &mut printed, |print| {
                call.resume(MontyException::new(ExcType::PermissionError, Some("Host filesystem, environment, network and process access is unavailable; use an exposed tool".to_owned())), print)
            }),
            RunProgress::Complete(_) => unreachable!(),
        };
    }
}

fn positive_limit(request: &Value, key: &str, default: u64) -> Result<u64, String> {
    match request.get(key) {
        None => Ok(default),
        Some(value) => value
            .as_u64()
            .filter(|v| *v > 0)
            .ok_or_else(|| format!("{key} must be positive")),
    }
}

fn step(
    transport: &mut impl Transport,
    printed: &mut usize,
    run: impl FnOnce(PrintWriter<'_>) -> Result<RunProgress, MontyException>,
) -> Result<RunProgress, MontyException> {
    let mut writer = Printer { transport, printed };
    run(PrintWriter::Callback(&mut writer))
}

fn report_error(
    transport: &mut impl Transport,
    error: MontyException,
    syntax: bool,
) -> Result<(), String> {
    let text = prefix(&error.to_string(), OUTPUT_UTF16_LIMIT);
    transport.send(json!({"kind":"error","syntax":syntax,"text":text}))
}

struct Printer<'a, T> {
    transport: &'a mut T,
    printed: &'a mut usize,
}
impl<T: Transport> PrintWriterCallback for Printer<'_, T> {
    fn stdout_write(&mut self, output: Cow<'_, str>) -> Result<(), MontyException> {
        let room = OUTPUT_UTF16_LIMIT.saturating_sub(*self.printed);
        if room > 0 {
            let text = prefix(&output, room);
            *self.printed += text.encode_utf16().count();
            if !text.is_empty() {
                self.transport
                    .send(json!({"kind":"print","text":text}))
                    .map_err(MontyException::runtime_error)?;
            }
        }
        Ok(())
    }
    fn stdout_push(&mut self, end: char) -> Result<(), MontyException> {
        self.stdout_write(Cow::Owned(end.to_string()))
    }
}

fn prefix(text: &str, limit: usize) -> String {
    let mut count = 0;
    text.chars()
        .take_while(|c| {
            count += c.len_utf16();
            count <= limit
        })
        .collect()
}

fn to_json(value: &MontyObject, depth: usize) -> Result<Value, String> {
    if depth > 64 {
        return Err("Tool arguments exceed the maximum JSON depth".to_owned());
    }
    match value {
        MontyObject::None => Ok(Value::Null),
        MontyObject::Bool(value) => Ok(json!(value)),
        MontyObject::Int(value) => Ok(json!(value)),
        MontyObject::BigInt(value) => {
            serde_json::from_str(&value.to_string()).map_err(|error| error.to_string())
        }
        MontyObject::Float(value) if value.is_finite() => Ok(json!(value)),
        MontyObject::String(value) => Ok(json!(value)),
        MontyObject::List(values) | MontyObject::Tuple(values) => values
            .iter()
            .map(|v| to_json(v, depth + 1))
            .collect::<Result<Vec<_>, _>>()
            .map(Value::Array),
        MontyObject::Dict(values) => {
            let mut object = Map::new();
            for (key, value) in values {
                let MontyObject::String(key) = key else {
                    return Err("Tool JSON objects require string keys".to_owned());
                };
                object.insert(key.clone(), to_json(value, depth + 1)?);
            }
            Ok(Value::Object(object))
        }
        _ => Err("Tool arguments must contain only finite JSON values".to_owned()),
    }
}

fn from_json(value: &Value, depth: usize) -> Result<MontyObject, String> {
    if depth > 64 {
        return Err("Tool results exceed the maximum JSON depth".to_owned());
    }
    Ok(match value {
        Value::Null => MontyObject::None,
        Value::Bool(value) => MontyObject::Bool(*value),
        Value::String(value) => MontyObject::String(value.clone()),
        Value::Number(value) => {
            if let Some(value) = value.as_i64() {
                MontyObject::Int(value)
            } else if let Ok(integer) = value.to_string().parse::<num_bigint::BigInt>() {
                MontyObject::BigInt(integer)
            } else {
                MontyObject::Float(
                    value
                        .as_f64()
                        .filter(|v| v.is_finite())
                        .ok_or("Nonfinite numeric result")?,
                )
            }
        }
        Value::Array(values) => MontyObject::List(
            values
                .iter()
                .map(|v| from_json(v, depth + 1))
                .collect::<Result<Vec<_>, _>>()?,
        ),
        Value::Object(values) => MontyObject::dict(
            values
                .iter()
                .map(|(key, value)| {
                    Ok((
                        MontyObject::String(key.clone()),
                        from_json(value, depth + 1)?,
                    ))
                })
                .collect::<Result<Vec<_>, String>>()?,
        ),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[derive(Default)]
    struct RecordingTransport {
        events: Vec<Value>,
    }
    impl Transport for RecordingTransport {
        fn send(&mut self, event: Value) -> Result<(), String> {
            self.events.push(event);
            Ok(())
        }
        fn receive(&mut self) -> Result<Value, String> {
            Err("No host response expected".to_owned())
        }
    }

    #[test]
    fn executes_real_monty_and_returns_only_print_output() {
        let mut transport = RecordingTransport::default();
        run(
            json!({"code":"print(sum(x * x for x in range(5)))\n42", "functions":[]}),
            &mut transport,
        )
        .unwrap();
        assert_eq!(transport.events.last().unwrap()["kind"], "complete");
        let output: String = transport
            .events
            .iter()
            .filter(|v| v["kind"] == "print")
            .map(|v| v["text"].as_str().unwrap())
            .collect();
        assert_eq!(output, "30\n");
    }

    #[test]
    fn genuine_async_gather_resolves_out_of_order_without_reordering_values() {
        struct Host {
            events: Vec<Value>,
            pending: Vec<Value>,
        }
        impl Transport for Host {
            fn send(&mut self, event: Value) -> Result<(), String> {
                if event["kind"] == "call" {
                    self.pending.push(json!({"id":event["id"],"value":{"answer":event["kwargs"]["x"].as_i64().unwrap() + 1}}));
                }
                self.events.push(event);
                Ok(())
            }
            fn receive(&mut self) -> Result<Value, String> {
                Ok(json!({"results":[self.pending.pop().unwrap()]}))
            }
        }
        let mut host = Host {
            events: vec![],
            pending: vec![],
        };
        run(json!({"code":"import asyncio\nanswers = await asyncio.gather(read(x=1), read(x=2))\nprint([x['answer'] for x in answers])", "functions":["read"]}), &mut host).unwrap();
        assert_eq!(host.events.last().unwrap()["kind"], "complete");
        assert_eq!(
            host.events.iter().filter(|v| v["kind"] == "call").count(),
            2
        );
        let output: String = host
            .events
            .iter()
            .filter(|v| v["kind"] == "print")
            .map(|v| v["text"].as_str().unwrap())
            .collect();
        assert_eq!(output, "[2, 3]\n");
    }

    #[test]
    fn guest_cannot_read_host_files_or_invent_external_functions() {
        for code in [
            "open('/etc/passwd').read()",
            "import os\nos.getenv('PATH')",
            "await missing_tool()",
        ] {
            let mut transport = RecordingTransport::default();
            run(json!({"code":code,"functions":[]}), &mut transport).unwrap();
            assert_eq!(transport.events.last().unwrap()["kind"], "error");
            assert!(!transport.events.iter().any(|v| v["kind"] == "call"));
        }
    }

    #[test]
    fn syntax_runtime_and_uncatchable_execution_budget_errors_are_distinct() {
        for (code, syntax, text) in [
            ("if :", true, "SyntaxError"),
            ("print('before')\n1/0", false, "ZeroDivisionError"),
            ("while True: pass", false, "time limit"),
        ] {
            let mut transport = RecordingTransport::default();
            run(
                json!({"code":code,"functions":[],"max_duration_ms":5}),
                &mut transport,
            )
            .unwrap();
            let event = transport.events.last().unwrap();
            assert_eq!(event["kind"], "error");
            assert_eq!(event["syntax"], syntax);
            assert!(event["text"].as_str().unwrap().contains(text), "{event}");
        }
    }

    #[test]
    fn structured_json_round_trips_large_integers_and_rejects_cycles_or_nonfinite_values() {
        let value = json!({"large":18446744073709551615_u64,"null":null,"list":[true,"雪"]});
        assert_eq!(to_json(&from_json(&value, 0).unwrap(), 0).unwrap(), value);
        assert!(to_json(&MontyObject::Float(f64::INFINITY), 0).is_err());
        assert!(to_json(&MontyObject::Cycle(1, "[...]".to_owned()), 0).is_err());
    }
}
