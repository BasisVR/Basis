// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

use bytes::Bytes;
use url::Url;

use crate::rtsp::msg::{self, OwnedMessage, StatusCode};

use super::{ResponseMode, RtspConnection, SessionOptions, Tool};
use crate::{Error, error::ErrorInt};

const EXISTING_CONN_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(5);
const FRESH_CONN_INITIAL_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(1);
const FRESH_CONN_MAX_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(16);

/// Handles `TEARDOWN` loop in the background, removing the stale session entry on success or
/// session expiry.
#[allow(clippy::too_many_arguments)]
pub(super) async fn background_teardown(
    seqnum: Option<u64>,
    base_url: Url,
    tool: Option<Tool>,
    session_id: Box<str>,
    just_try_once: bool,
    options: SessionOptions,
    requested_auth: Option<http_auth::PasswordClient>,
    conn: Option<RtspConnection>,
    mut tx: tokio::sync::watch::Sender<Option<Result<(), Error>>>,
    expires: tokio::time::Instant,
) {
    log::debug!(
        "TEARDOWN {} starting for URL {}",
        &*session_id,
        base_url.as_str(),
    );
    if tokio::time::timeout_at(
        expires,
        teardown_loop_forever(
            base_url,
            tool,
            &session_id,
            just_try_once,
            &options,
            requested_auth,
            conn,
            &mut tx,
        ),
    )
    .await
    .is_err()
    {
        log::debug!("TEARDOWN {} aborted on session expiration", &*session_id);
    }
    if let Some(ref session_group) = options.session_group {
        let seqnum = seqnum.expect("seqnum specified when session_group exists");
        log::trace!(
            "Clearing session {:?}/{} for id {:?}",
            session_group.debug_id(),
            seqnum,
            &*session_id
        );
        if !session_group.try_remove_seqnum(seqnum) {
            log::warn!(
                "Unable to find session {:?}/{} for id {:?} on TEARDOWN",
                session_group.debug_id(),
                seqnum,
                &*session_id
            );
        }
    }

    let _ = tx.send(Some(Ok(())));
}

/// Attempts `TEARDOWN` in a loop until success; caller should bound by session expiry.
#[allow(clippy::too_many_arguments)]
pub(super) async fn teardown_loop_forever(
    url: Url,
    tool: Option<Tool>,
    session_id: &str,
    just_try_once: bool,
    options: &SessionOptions,
    mut requested_auth: Option<http_auth::PasswordClient>,
    mut conn: Option<RtspConnection>,
    tx: &mut tokio::sync::watch::Sender<Option<Result<(), Error>>>,
) {
    let mut req = OwnedMessage::Request {
        head: msg::Request {
            method: msg::Method::TEARDOWN,
            request_uri: Some(url.clone()),
            headers: [(
                msg::HeaderName::SESSION,
                msg::HeaderValue::try_from(session_id.to_string()).unwrap(),
            )]
            .into(),
        },
        body: Bytes::new(),
    };

    let attempt_deadline = tokio::time::sleep(EXISTING_CONN_TIMEOUT);
    tokio::pin!(attempt_deadline);
    if let Some(conn) = conn.take() {
        // Attempt first on existing connection. Besides being the most
        // efficient approach, this is the best for old live555 servers' stale
        // TCP sessions. Tearing them down before closing the connection means
        // they don't have a chance to mess up any other sockets.
        tokio::select! {
            biased;
            r = attempt(&mut req, tool.as_ref(), options, &mut requested_auth, conn) => {
                match r {
                    Ok(status) => {
                        log::debug!("TEARDOWN {} on existing conn succeeded (status {}).", session_id, u16::from(status));
                        return
                    },
                    Err(e) => {
                        // Retry with a new connection, without waiting for the
                        // attempt deadline.
                        //
                        // A particularly likely case is when the session was
                        // dropped due to corrupt message. This attempt will
                        // inevitably fail reading the same corrupt message.
                        // It's worth trying anyway because the server may
                        // actually execute the TEARDOWN, even though we can't
                        // read the response.
                        //
                        // The first try with a fresh connection is likely
                        // to be more successful, and maybe will simply confirm
                        // the session is already destroyed. Do it soon.
                        //
                        // Also, don't update tx, so await_teardown() won't
                        // fail early. Let's at least do an attempt with a fresh
                        // connection first.
                        log::debug!("TEARDOWN {} on existing conn failed: {}", session_id, &e);
                    },
                }
            },
            _ = &mut attempt_deadline => log::debug!("TEARDOWN {} on existing conn timed out", session_id),
        }
    };

    if just_try_once {
        // TCP, auto teardown, server not known to be affected by the live555
        // TCP session bug, tried one TEARDOWN on the existingconn if any (just in case the server
        // really does have that bug), closed the connection. Good enough.
        log::debug!(
            "Giving up on TEARDOWN {}; use TearDownPolicy::Always to try harder",
            session_id
        );
        return;
    }

    // Now retry with a fresh connection each time, giving longer times to
    // subsequent attempts.
    let mut timeout = FRESH_CONN_INITIAL_TIMEOUT;
    for attempt_num in 1.. {
        attempt_deadline
            .as_mut()
            .reset(tokio::time::Instant::now() + timeout);
        let attempt = async {
            let conn = RtspConnection::connect(&url, &options.connect_addrs).await?;
            attempt(&mut req, tool.as_ref(), options, &mut requested_auth, conn).await
        };
        tokio::select! {
            biased;
            r = attempt => {
                match r {
                    Ok(status) => {
                        log::debug!("TEARDOWN {} fresh connection attempt {} succeeded (status {}).", session_id, attempt_num, u16::from(status));
                        return
                    },
                    Err(e) => {
                        log::debug!("TEARDOWN {} fresh connection attempt {} failed: {}", session_id, attempt_num, &e);
                        let _ = tx.send(Some(Err(e)));

                        // Wait out the remaining time before trying again, to
                        // avoid going crazy when the server fails quickly.
                        attempt_deadline.as_mut().await;
                    },
                }
            },
            _ = &mut attempt_deadline => {
                log::debug!("TEARDOWN {} fresh connection attempt {} timed out", session_id, attempt_num);
                let _ = tx.send(Some(Err(wrap!(ErrorInt::Timeout))));
            },
        }
        timeout = std::cmp::min(timeout * 2, FRESH_CONN_MAX_TIMEOUT);
    }
}

/// Makes a single attempt on the supplied connection; caller is responsible for the timeout.
async fn attempt(
    req: &mut OwnedMessage,
    tool: Option<&Tool>,
    options: &SessionOptions,
    requested_auth: &mut Option<http_auth::PasswordClient>,
    mut conn: RtspConnection,
) -> Result<StatusCode, Error> {
    let e = match conn
        .send(ResponseMode::Teardown, options, tool, requested_auth, req)
        .await
    {
        Ok((_ctx, _cseq, resp, _body)) => return Ok(resp.status_code),
        Err(e) => e,
    };

    // Use a second match clause to look inside the Arc.
    match *e.0 {
        ErrorInt::RtspResponseError { status, .. }
            if status == StatusCode::SESSION_NOT_FOUND ||

            // This is deeply unsatisfying, but at least the Hikvision
            // DS-2CD2032 with firmware V5.4.41 build 170312 exhibits the
            // following non-standard behavior:
            //
            // * the RTSP session is tied to a particular connection, even when
            //   streaming RTP over UDP. If the connection is dropped, the UDP
            //   packets stop flowing.
            // * a TEARDOWN on a fresh connection will always fail with
            //   status 500 Internal Server Error.
            //
            // This contradicts RFC 2326 section 1.1, which says "An RTSP
            // session is in no way tied to a transport-level connection such as
            // a TCP connection". Nonetheless, we'll go along with this for now
            // by assuming the session is gone when we get a 500 response.
            status == StatusCode::INTERNAL_SERVER_ERROR =>
        {
            Ok(status)
        }
        _ => Err(e),
    }
}
