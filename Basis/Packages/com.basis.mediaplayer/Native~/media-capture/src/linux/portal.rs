use std::collections::HashMap;
use std::os::fd::OwnedFd;
use std::pin::Pin;
use std::sync::atomic::{AtomicU32, Ordering};
use std::task::{Context, Poll, Waker};
use std::time::{Duration, Instant};

use zbus::blocking::Connection;
use zbus::export::futures_core::Stream;
use zbus::message::Type;
use zbus::zvariant::{ObjectPath, OwnedObjectPath, OwnedValue, Value};
use zbus::{MatchRule, Message, MessageStream};

use crate::CaptureError;

const DESTINATION: &str = "org.freedesktop.portal.Desktop";
const DESKTOP_PATH: &str = "/org/freedesktop/portal/desktop";
const SCREENCAST: &str = "org.freedesktop.portal.ScreenCast";
const REQUEST: &str = "org.freedesktop.portal.Request";
const SESSION: &str = "org.freedesktop.portal.Session";
const SOURCE_MONITOR: u32 = 1;
const SOURCE_WINDOW: u32 = 2;
const CURSOR_HIDDEN: u32 = 1;
const CURSOR_EMBEDDED: u32 = 2;
const RESPONSE_TIMEOUT: Duration = Duration::from_secs(120);
const POLL: Duration = Duration::from_millis(50);

struct Session {
    conn: Connection,
    path: OwnedObjectPath,
}

impl Drop for Session {
    fn drop(&mut self) {
        let _ = self.conn.call_method(
            Some(DESTINATION),
            self.path.as_str(),
            Some(SESSION),
            "Close",
            &(),
        );
    }
}

pub(crate) struct PortalCast {
    pub node_id: u32,
    pub fd: OwnedFd,
    pub width: u32,
    pub height: u32,
    _session: Session,
}

type Options<'a> = HashMap<&'static str, Value<'a>>;
type Results = HashMap<String, OwnedValue>;

fn err(what: &str) -> impl FnOnce(zbus::Error) -> CaptureError + '_ {
    move |e| CaptureError(format!("{what}: {e}"))
}

fn property_u32(conn: &Connection, name: &str) -> Option<u32> {
    let reply = conn
        .call_method(
            Some(DESTINATION),
            DESKTOP_PATH,
            Some("org.freedesktop.DBus.Properties"),
            "Get",
            &(SCREENCAST, name),
        )
        .ok()?;
    let value: OwnedValue = reply.body().deserialize().ok()?;
    match unwrap_variant(&value) {
        Value::U32(v) => Some(*v),
        _ => None,
    }
}

fn unwrap_variant<'a>(mut value: &'a Value<'a>) -> &'a Value<'a> {
    while let Value::Value(inner) = value {
        value = inner;
    }
    value
}

pub(crate) fn available() -> bool {
    Connection::session()
        .ok()
        .and_then(|conn| property_u32(&conn, "version"))
        .is_some_and(|v| v >= 1)
}

fn sender_path_part(unique: &str) -> String {
    unique.trim_start_matches(':').replace('.', "_")
}

fn token() -> String {
    static COUNTER: AtomicU32 = AtomicU32::new(0);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.subsec_nanos());
    format!(
        "basis_{}_{}_{nanos}",
        std::process::id(),
        COUNTER.fetch_add(1, Ordering::Relaxed)
    )
}

fn request_path(sender: &str, token: &str) -> String {
    format!("{DESKTOP_PATH}/request/{sender}/{token}")
}

fn session_path(sender: &str, token: &str) -> String {
    format!("{DESKTOP_PATH}/session/{sender}/{token}")
}

fn cursor_mode(cursor: bool, available: Option<u32>) -> Option<u32> {
    let available = available?;
    let wanted = if cursor {
        CURSOR_EMBEDDED
    } else {
        CURSOR_HIDDEN
    };
    if available & wanted != 0 {
        Some(wanted)
    } else if available & CURSOR_HIDDEN != 0 {
        Some(CURSOR_HIDDEN)
    } else {
        None
    }
}

fn source_types(available: Option<u32>) -> u32 {
    let wanted = SOURCE_MONITOR | SOURCE_WINDOW;
    match available.map(|a| a & wanted) {
        Some(0) => SOURCE_MONITOR,
        Some(types) => types,
        None => wanted,
    }
}

struct Responses {
    stream: MessageStream,
}

impl Responses {
    fn subscribe(conn: &Connection) -> Result<Self, CaptureError> {
        let rule = MatchRule::builder()
            .msg_type(Type::Signal)
            .interface(REQUEST)
            .and_then(|b| b.member("Response"))
            .map_err(err("portal match rule"))?
            .build();
        let stream = zbus::block_on(MessageStream::for_match_rule(rule, conn.inner(), Some(64)))
            .map_err(err("subscribe to portal responses"))?;
        Ok(Self { stream })
    }

    fn poll(&mut self) -> Option<Result<Message, zbus::Error>> {
        let mut cx = Context::from_waker(Waker::noop());
        match Pin::new(&mut self.stream).poll_next(&mut cx) {
            Poll::Ready(Some(msg)) => Some(msg),
            Poll::Ready(None) => Some(Err(zbus::Error::Failure("D-Bus connection closed".into()))),
            Poll::Pending => None,
        }
    }

    fn wait(
        &mut self,
        conn: &Connection,
        path: &str,
        what: &str,
        stop: &dyn Fn() -> bool,
    ) -> Result<Results, CaptureError> {
        let deadline = Instant::now() + RESPONSE_TIMEOUT;
        loop {
            while let Some(msg) = self.poll() {
                let msg = msg.map_err(err(what))?;
                if msg.header().path().is_none_or(|p| p.as_str() != path) {
                    continue;
                }
                let (code, results): (u32, Results) =
                    msg.body().deserialize().map_err(err(what))?;
                return match code {
                    0 => Ok(results),
                    1 => Err(CaptureError(format!("{what}: cancelled by the user"))),
                    _ => Err(CaptureError(format!(
                        "{what}: the desktop portal reported an error"
                    ))),
                };
            }
            let timed_out = Instant::now() >= deadline;
            if timed_out || stop() {
                let _ = conn.call_method(Some(DESTINATION), path, Some(REQUEST), "Close", &());
                return Err(CaptureError(if timed_out {
                    format!("{what}: no answer from the desktop portal after 120 s")
                } else {
                    format!("{what}: cancelled")
                }));
            }
            std::thread::sleep(POLL);
        }
    }
}

fn call_request(
    conn: &Connection,
    responses: &mut Responses,
    sender: &str,
    method: &str,
    body: &(impl serde::Serialize + zbus::zvariant::DynamicType),
    token: &str,
    stop: &dyn Fn() -> bool,
) -> Result<Results, CaptureError> {
    let what = format!("screen cast {method}");
    let reply = conn
        .call_method(
            Some(DESTINATION),
            DESKTOP_PATH,
            Some(SCREENCAST),
            method,
            body,
        )
        .map_err(err(&what))?;
    let handle: OwnedObjectPath = reply.body().deserialize().map_err(err(&what))?;
    let expected = request_path(sender, token);
    if handle.as_str() != expected {
        media_diag::diag_log!(
            "capture: portal request path {} differs from {expected}",
            handle.as_str()
        );
    }
    responses.wait(conn, handle.as_str(), &what, stop)
}

fn session_handle(results: &Results) -> Option<String> {
    match unwrap_variant(results.get("session_handle")?) {
        Value::Str(s) => Some(s.to_string()),
        Value::ObjectPath(p) => Some(p.to_string()),
        _ => None,
    }
}

fn first_stream(results: &Results) -> Option<(u32, Option<(u32, u32)>)> {
    let Value::Array(streams) = unwrap_variant(results.get("streams")?) else {
        return None;
    };
    let Value::Structure(stream) = unwrap_variant(streams.inner().first()?) else {
        return None;
    };
    let fields = stream.fields();
    let Value::U32(node) = unwrap_variant(fields.first()?) else {
        return None;
    };
    let size = match fields.get(1).map(unwrap_variant) {
        Some(Value::Dict(props)) => props
            .iter()
            .find_map(|(k, v)| match (k, unwrap_variant(v)) {
                (Value::Str(k), Value::Structure(s)) if k.as_str() == "size" => {
                    match (
                        s.fields().first().map(unwrap_variant),
                        s.fields().get(1).map(unwrap_variant),
                    ) {
                        (Some(Value::I32(w)), Some(Value::I32(h))) if *w > 0 && *h > 0 => {
                            Some((*w as u32, *h as u32))
                        }
                        _ => None,
                    }
                }
                _ => None,
            }),
        _ => None,
    };
    Some((*node, size))
}

pub(crate) fn request_screencast(
    cursor: bool,
    stop: &dyn Fn() -> bool,
) -> Result<PortalCast, CaptureError> {
    let conn = Connection::session().map_err(err("connect to the session bus"))?;
    let version = property_u32(&conn, "version").unwrap_or(0);
    if version < 1 {
        return Err(CaptureError(
            "screen sharing needs xdg-desktop-portal with ScreenCast support".into(),
        ));
    }
    let sender = sender_path_part(
        conn.unique_name()
            .ok_or_else(|| CaptureError("session bus gave no unique name".into()))?
            .as_str(),
    );
    let mut responses = Responses::subscribe(&conn)?;

    let request_token = token();
    let session_token = token();
    let options: Options<'_> = HashMap::from([
        ("handle_token", Value::from(request_token.as_str())),
        ("session_handle_token", Value::from(session_token.as_str())),
    ]);
    let created = call_request(
        &conn,
        &mut responses,
        &sender,
        "CreateSession",
        &(options,),
        &request_token,
        stop,
    )?;
    let path = session_handle(&created).unwrap_or_else(|| session_path(&sender, &session_token));
    let path = OwnedObjectPath::try_from(path)
        .map_err(|e| CaptureError(format!("portal session handle: {e}")))?;
    let session = Session {
        conn: conn.clone(),
        path,
    };
    let session_ref: &ObjectPath<'_> = &session.path;

    let request_token = token();
    let mut options: Options<'_> = HashMap::from([
        ("handle_token", Value::from(request_token.as_str())),
        (
            "types",
            Value::U32(source_types(property_u32(&conn, "AvailableSourceTypes"))),
        ),
        ("multiple", Value::Bool(false)),
    ]);
    if let Some(mode) = cursor_mode(cursor, property_u32(&conn, "AvailableCursorModes")) {
        options.insert("cursor_mode", Value::U32(mode));
    }
    if version >= 4 {
        options.insert("persist_mode", Value::U32(0));
    }
    call_request(
        &conn,
        &mut responses,
        &sender,
        "SelectSources",
        &(session_ref, options),
        &request_token,
        stop,
    )?;

    let request_token = token();
    let options: Options<'_> =
        HashMap::from([("handle_token", Value::from(request_token.as_str()))]);
    let started = call_request(
        &conn,
        &mut responses,
        &sender,
        "Start",
        &(session_ref, "", options),
        &request_token,
        stop,
    )?;
    let (node_id, size) = first_stream(&started)
        .ok_or_else(|| CaptureError("the desktop portal shared no stream".into()))?;

    let options: Options<'_> = HashMap::new();
    let reply = conn
        .call_method(
            Some(DESTINATION),
            DESKTOP_PATH,
            Some(SCREENCAST),
            "OpenPipeWireRemote",
            &(session_ref, options),
        )
        .map_err(err("open the PipeWire remote"))?;
    let fd: zbus::zvariant::OwnedFd = reply
        .body()
        .deserialize()
        .map_err(err("open the PipeWire remote"))?;
    let (width, height) = size.unwrap_or((0, 0));
    Ok(PortalCast {
        node_id,
        fd: fd.into(),
        width,
        height,
        _session: session,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_follow_the_portal_spec() {
        assert_eq!(sender_path_part(":1.42"), "1_42");
        assert_eq!(
            request_path("1_42", "basis_1"),
            "/org/freedesktop/portal/desktop/request/1_42/basis_1"
        );
        assert_eq!(
            session_path("1_42", "basis_2"),
            "/org/freedesktop/portal/desktop/session/1_42/basis_2"
        );
        let t = token();
        assert!(t.chars().all(|c| c.is_ascii_alphanumeric() || c == '_'));
        assert_ne!(t, token());
    }

    #[test]
    fn cursor_and_source_choices() {
        assert_eq!(cursor_mode(true, Some(7)), Some(CURSOR_EMBEDDED));
        assert_eq!(cursor_mode(false, Some(7)), Some(CURSOR_HIDDEN));
        assert_eq!(cursor_mode(true, Some(1)), Some(CURSOR_HIDDEN));
        assert_eq!(cursor_mode(true, Some(4)), None);
        assert_eq!(cursor_mode(true, None), None);
        assert_eq!(source_types(Some(7)), 3);
        assert_eq!(source_types(Some(1)), 1);
        assert_eq!(source_types(Some(4)), 1);
        assert_eq!(source_types(None), 3);
    }
}
