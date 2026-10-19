//! `ResourceFetcher`: the byte cap that bounds how much attacker-chosen
//! data a playlist lane will buffer, the origin split that decides
//! whether the lane has a filesystem arm at all, and connection reuse
//! across a live playlist's fetches.

use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::thread;

use media_hls::SegmentFetcher;
use media_io::{
    AddressGate, AllowAllGate, CancelToken, IoError, IoErrorKind, IoLimits, PublicAddressGate,
    ResourceFetcher,
};

/// Per-process scratch directory, created on first use.
fn scratch_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!("bm-fetch-test-{}", std::process::id()));
    std::fs::create_dir_all(&dir).expect("scratch dir");
    dir
}

/// A fetcher for a playlist opened from disk, rooted at the scratch
/// directory the fixtures below are written into.
fn local_fetcher() -> ResourceFetcher {
    ResourceFetcher::local(
        &scratch_dir(),
        IoLimits::default(),
        Arc::new(PublicAddressGate),
        CancelToken::new(),
    )
    .expect("scratch dir canonicalises")
}

/// A fetcher for a playlist that came off the network.
fn remote_fetcher() -> ResourceFetcher {
    ResourceFetcher::remote(
        IoLimits::default(),
        Arc::new(PublicAddressGate),
        CancelToken::new(),
    )
}

/// A file of `len` bytes in the scratch directory, returned as the string
/// the fetcher takes.
fn scratch_file(name: &str, len: usize) -> (PathBuf, String) {
    let path = scratch_dir().join(name);
    std::fs::write(&path, vec![b'x'; len]).expect("write scratch file");
    let url = path.to_str().expect("scratch path is UTF-8").to_owned();
    (path, url)
}

/// A resource at exactly the cap is served whole: the bound is inclusive,
/// so the boundary case is playable rather than a spurious refusal.
#[test]
fn a_file_at_the_cap_is_served_whole() {
    let (path, url) = scratch_file("at-cap.bin", 64);
    let bytes = local_fetcher()
        .fetch(&url, 64)
        .expect("at the cap, so served");
    assert_eq!(bytes.len(), 64, "every byte of the resource");
    let _ = std::fs::remove_file(path);
}

/// One byte past the cap refuses. The read is bounded at `cap + 1`, so
/// this is the first length that can trip the post-read check, and an
/// off-by-one in either direction shows up here.
#[test]
fn a_file_past_the_cap_refuses() {
    let (path, url) = scratch_file("past-cap.bin", 65);
    let err = local_fetcher()
        .fetch(&url, 64)
        .expect_err("past the cap, so refused");
    assert!(
        err.to_string().contains("exceeds the 64-byte cap"),
        "refusal names the cap it enforced: {err}"
    );
    let _ = std::fs::remove_file(path);
}

/// The cap binds the bytes returned, not the length the filesystem stated
/// for the path. Nothing the fetcher hands back may exceed it, which is
/// what keeps a file that grows (or is swapped) after the size is taken
/// from turning a bounded fetch into an unbounded allocation. The race
/// itself is not reproducible in-process; this pins the invariant that
/// makes losing it harmless.
#[test]
fn no_fetch_returns_more_than_its_cap() {
    let (path, url) = scratch_file("bound.bin", 4096);
    for cap in [0u64, 1, 255, 4095, 4096] {
        match local_fetcher().fetch(&url, cap) {
            Ok(bytes) => assert!(
                bytes.len() as u64 <= cap,
                "served {} bytes against a {cap}-byte cap",
                bytes.len()
            ),
            Err(e) => assert!(
                e.to_string().contains("exceeds the"),
                "refused for a reason other than the cap: {e}"
            ),
        }
    }
    let _ = std::fs::remove_file(path);
}

/// A resource that is not there fails as an I/O error naming the path,
/// not as a cap refusal — the two are different diagnoses and the lane
/// reports them separately.
#[test]
fn a_missing_file_is_an_io_error_not_a_cap_refusal() {
    let missing = scratch_dir().join("not-here.bin");
    let url = missing.to_str().expect("path is UTF-8");
    let err = local_fetcher().fetch(url, 64).expect_err("no such file");
    assert!(
        !err.to_string().contains("exceeds the"),
        "a missing file is not a cap refusal: {err}"
    );
}

/// A resource outside the playlist's own directory is refused even
/// though the fetcher has a filesystem arm and the file is readable. The
/// disk-origin lane is confined to where the playlist sits, not given
/// the whole filesystem.
#[test]
fn a_disk_playlist_cannot_read_outside_its_directory() {
    let outside = scratch_dir().join("outside.bin");
    std::fs::write(&outside, b"secret").expect("write outside the root");
    let root = scratch_dir().join("playlist-dir");
    std::fs::create_dir_all(&root).expect("playlist dir");
    let fetcher = || {
        ResourceFetcher::local(
            &root,
            IoLimits::default(),
            Arc::new(PublicAddressGate),
            CancelToken::new(),
        )
        .expect("root canonicalises")
    };

    // Absolute, and a walk back out through the parent. Both name the
    // same readable file outside the root, and different screens catch
    // them, so each case names the refusal it expects ("directory" alone
    // appears in all three and would not tell them apart).
    let absolute = outside.to_str().expect("UTF-8").to_owned();
    let traversal = root
        .join("..")
        .join("outside.bin")
        .to_str()
        .expect("UTF-8")
        .to_owned();
    for (url, expected) in [
        (absolute, "outside the playlist's directory"),
        (traversal, "walks out of its directory"),
    ] {
        let Err(err) = fetcher().fetch(&url, 64) else {
            panic!("{url:?} must refuse, not serve");
        };
        assert!(
            err.to_string().contains(expected),
            "{url:?} refused as {err}, not {expected:?}"
        );
    }

    // The control: the same bytes inside the root do serve, so the
    // refusals above are the confinement and not a broken lane.
    let inside = root.join("inside.bin");
    std::fs::write(&inside, b"secret").expect("write inside the root");
    let url = inside.to_str().expect("UTF-8").to_owned();
    assert_eq!(
        fetcher().fetch(&url, 64).expect("inside the root").len(),
        6,
        "the confined lane still serves its own directory"
    );
}

/// Remove a planted link by what it actually is. A Unix symlink needs
/// `remove_file` (`remove_dir` refuses it with ENOTDIR, and `remove_dir_all`
/// will not follow it), while a Windows junction needs `remove_dir`. A link
/// left behind makes the next run fail to plant one, print SKIPPED and
/// assert nothing.
fn remove_planted_link(link: &std::path::Path) {
    if link.symlink_metadata().is_err() {
        return;
    }
    if std::fs::remove_file(link).is_ok() {
        return;
    }
    let _ = std::fs::remove_dir(link);
}

/// Plant a directory link inside `root` pointing at `target`. Windows
/// symlinks need Developer Mode or elevation, but a directory junction
/// needs neither and `canonicalize` resolves both, so fall back to one
/// rather than let the row quietly stop running on an ordinary box.
/// Returns false when the host allows neither.
#[cfg(unix)]
fn plant_directory_link(link: &std::path::Path, target: &std::path::Path) -> bool {
    std::os::unix::fs::symlink(target, link).is_ok()
}

/// As above. Defined per platform rather than branched inside one body,
/// so neither arm carries a `return` the other one needs.
#[cfg(windows)]
fn plant_directory_link(link: &std::path::Path, target: &std::path::Path) -> bool {
    if std::os::windows::fs::symlink_dir(target, link).is_ok() {
        return true;
    }
    std::process::Command::new("cmd")
        .args(["/C", "mklink", "/J"])
        .arg(link)
        .arg(target)
        .output()
        .map(|out| out.status.success())
        .unwrap_or(false)
}

/// A link planted inside the playlist's directory that points outside it
/// is refused. Every component is an ordinary name under the root, so the
/// lexical screen passes it and the canonicalising screen has to catch it.
#[test]
fn a_link_out_of_the_root_is_refused() {
    let outside = scratch_dir().join("link-target-dir");
    std::fs::create_dir_all(&outside).expect("target dir");
    std::fs::write(outside.join("secret.bin"), b"secret").expect("write the target");
    let root = scratch_dir().join("link-root");
    std::fs::create_dir_all(&root).expect("playlist dir");
    let link = root.join("escape");
    remove_planted_link(&link);
    if !plant_directory_link(&link, &outside) {
        eprintln!("SKIPPED: this host allows neither a symlink nor a junction");
        return;
    }

    let mut fetcher = ResourceFetcher::local(
        &root,
        IoLimits::default(),
        Arc::new(PublicAddressGate),
        CancelToken::new(),
    )
    .expect("root canonicalises");
    let through_link = link.join("secret.bin");
    let url = through_link.to_str().expect("UTF-8").to_owned();
    let Err(err) = fetcher.fetch(&url, 64) else {
        panic!("a link out of the root must refuse, not serve");
    };
    assert!(
        err.to_string().contains("resolves outside"),
        "caught by the wrong screen, so the link was never resolved: {err}"
    );
    remove_planted_link(&link);
}

/// A playlist that came off the network has no filesystem arm, so a URI
/// it names cannot be read off disk however it is spelled. The file is
/// real and within what the local fetcher would serve, which is the
/// point: the refusal is the fetcher's origin, not a missing file.
#[test]
fn a_network_playlist_cannot_read_a_local_file() {
    let (path, url) = scratch_file("readable.bin", 16);
    assert_eq!(
        local_fetcher()
            .fetch(&url, 64)
            .expect("local lane serves it")
            .len(),
        16,
        "the same resource is served from a disk-origin playlist"
    );
    let err = remote_fetcher()
        .fetch(&url, 64)
        .expect_err("network origin has no filesystem arm");
    assert!(
        err.to_string().contains("may not name a local resource"),
        "refused as an origin violation: {err}"
    );
    let _ = std::fs::remove_file(path);
}

/// The spellings a hostile playlist reaches for. None of them are
/// `http://` or `https://`, so all of them land on the arm a
/// network-origin fetcher does not have. That includes the Windows
/// drive-relative form, which reads as a URL but resolves as a path, and
/// the UNC form, which would otherwise be an outbound SMB connect the
/// address gate never sees.
#[test]
fn a_network_playlist_refuses_every_non_http_spelling() {
    for url in [
        "c://Windows/win.ini",
        "C://Windows//System32/drivers/etc/hosts",
        "file:///etc/passwd",
        "\\\\attacker.example\\share\\clip.ts",
        "/etc/passwd",
        "../../../etc/passwd",
        "ftp://attacker.example/clip.ts",
    ] {
        let Err(err) = remote_fetcher().fetch(url, 64) else {
            panic!("{url:?} must refuse, not serve");
        };
        assert!(
            err.to_string().contains("may not name a local resource"),
            "{url:?} refused for the wrong reason: {err}"
        );
    }
}

/// The other side of that seam: a fetcher rooted at the current
/// directory serves a resource resolved against it. Cargo runs a test
/// with the package directory as the working directory, so the
/// manifest is certainly there and inside the root. The row is about the
/// two spellings agreeing, not the bytes.
#[test]
fn a_root_of_the_current_directory_serves_what_resolves_against_it() {
    let mut fetcher = ResourceFetcher::local(
        std::path::Path::new("."),
        IoLimits::default(),
        Arc::new(PublicAddressGate),
        CancelToken::new(),
    )
    .expect("the current directory canonicalises");
    let resolved = std::path::Path::new(".").join("Cargo.toml");
    let url = resolved.to_str().expect("UTF-8");
    let bytes = fetcher
        .fetch(url, 1024 * 1024)
        .unwrap_or_else(|e| panic!("{url:?} is inside the root and must serve: {e}"));
    assert!(!bytes.is_empty(), "served the manifest");
}

/// A keep-alive HTTP/1.1 server: `/missing` answers 404, any other path
/// answers 200 with `body`. Returns the base URL and a count of the
/// connections it has accepted.
fn spawn_keep_alive_server(body: Vec<u8>) -> (String, Arc<AtomicUsize>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let accepted = Arc::new(AtomicUsize::new(0));
    let counter = Arc::clone(&accepted);
    let body = Arc::new(body);
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            counter.fetch_add(1, Ordering::SeqCst);
            let body = Arc::clone(&body);
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut request_line = String::new();
                    if reader.read_line(&mut request_line).unwrap_or(0) == 0 {
                        return;
                    }
                    loop {
                        let mut header = String::new();
                        if reader.read_line(&mut header).unwrap_or(0) == 0 {
                            return;
                        }
                        if header == "\r\n" {
                            break;
                        }
                    }
                    let missing = request_line.contains(" /missing ");
                    let (status, payload): (&str, &[u8]) = if missing {
                        ("404 Not Found", b"")
                    } else {
                        ("200 OK", &body)
                    };
                    let head = format!(
                        "HTTP/1.1 {status}\r\nContent-Length: {}\r\n\r\n",
                        payload.len()
                    );
                    if stream.write_all(head.as_bytes()).is_err()
                        || stream.write_all(payload).is_err()
                    {
                        return;
                    }
                }
            });
        }
    });
    (format!("http://127.0.0.1:{port}"), accepted)
}

fn loopback_fetcher() -> ResourceFetcher {
    ResourceFetcher::remote(
        IoLimits::default(),
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
}

#[test]
fn network_fetches_to_one_host_share_a_connection() {
    let (base, accepted) = spawn_keep_alive_server(vec![7u8; 4096]);
    let mut fetcher = loopback_fetcher();
    for name in [
        "index.m3u8",
        "part0.mp4",
        "part1.mp4",
        "index.m3u8",
        "part2.mp4",
    ] {
        let bytes = fetcher
            .fetch(&format!("{base}/{name}"), 1 << 20)
            .expect("fetch");
        assert_eq!(bytes.len(), 4096);
    }
    assert_eq!(
        accepted.load(Ordering::SeqCst),
        1,
        "a live playlist's reloads and parts ride one connection"
    );
}

#[test]
fn a_network_body_past_the_cap_refuses() {
    let (base, _) = spawn_keep_alive_server(vec![1u8; 1001]);
    let mut fetcher = loopback_fetcher();
    assert_eq!(
        fetcher
            .fetch(&format!("{base}/at-cap"), 1001)
            .expect("at the cap")
            .len(),
        1001
    );
    let error = fetcher
        .fetch(&format!("{base}/past-cap"), 1000)
        .expect_err("one byte past the cap");
    assert!(error.to_string().contains("cap"), "{error}");
}

#[test]
fn a_network_error_status_is_an_http_error_with_its_status() {
    let (base, _) = spawn_keep_alive_server(vec![0u8; 16]);
    let mut fetcher = loopback_fetcher();
    let error = fetcher
        .fetch(&format!("{base}/missing"), 1 << 20)
        .expect_err("404");
    let io = error.downcast_ref::<IoError>().expect("an IoError");
    assert_eq!(io.kind, IoErrorKind::Http);
    assert_eq!(io.status, Some(404));
}

/// Permits everything and counts what it is asked, so a test can see when
/// a host's addresses are vetted again.
#[derive(Default)]
struct CountingGate(AtomicUsize);

impl AddressGate for CountingGate {
    fn permit(&self, _ip: std::net::IpAddr) -> bool {
        self.0.fetch_add(1, Ordering::SeqCst);
        true
    }
}

/// Accepts every connection and hangs up without answering, for as long
/// as the test runs. Returns its port.
fn spawn_hang_up_server() -> u16 {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        for stream in listener.incoming() {
            drop(stream);
        }
    });
    port
}

#[test]
fn a_transport_failure_makes_the_next_fetch_vet_the_host_again() {
    let (base, _) = spawn_keep_alive_server(vec![3u8; 64]);
    let port = base.rsplit(':').next().expect("port");
    let good = format!("http://localhost:{port}/segment");
    let closed = format!("http://localhost:{}/gone", spawn_hang_up_server());
    let gate = Arc::new(CountingGate::default());
    let mut fetcher = ResourceFetcher::remote(
        IoLimits::default(),
        Arc::clone(&gate) as Arc<dyn AddressGate>,
        CancelToken::new(),
    );
    let asked = || gate.0.load(Ordering::SeqCst);

    fetcher.fetch(&good, 1 << 20).expect("first fetch");
    let after_first = asked();
    fetcher.fetch(&good, 1 << 20).expect("second fetch");
    let reuse_cost = asked() - after_first;

    fetcher
        .fetch(&closed, 1 << 20)
        .expect_err("the server hangs up");
    let before_retry = asked();
    fetcher
        .fetch(&good, 1 << 20)
        .expect("fetch after the failure");
    assert!(
        asked() - before_retry > reuse_cost,
        "the fetch after a transport failure resolved and vetted the host again \
         (asked {} times, a reused client {reuse_cost})",
        asked() - before_retry
    );
}

#[test]
fn a_transport_failure_on_one_host_keeps_another_hosts_client() {
    let (base, _) = spawn_keep_alive_server(vec![3u8; 64]);
    let port = base.rsplit(':').next().expect("port");
    let good = format!("http://localhost:{port}/segment");
    let elsewhere = format!("http://127.0.0.1:{}/gone", spawn_hang_up_server());
    let gate = Arc::new(CountingGate::default());
    let mut fetcher = ResourceFetcher::remote(
        IoLimits::default(),
        Arc::clone(&gate) as Arc<dyn AddressGate>,
        CancelToken::new(),
    );
    let asked = || gate.0.load(Ordering::SeqCst);

    fetcher.fetch(&good, 1 << 20).expect("first fetch");
    let after_first = asked();
    fetcher.fetch(&good, 1 << 20).expect("second fetch");
    let reuse_cost = asked() - after_first;

    fetcher
        .fetch(&elsewhere, 1 << 20)
        .expect_err("the other host hangs up");
    let before_retry = asked();
    fetcher
        .fetch(&good, 1 << 20)
        .expect("fetch after the failure");
    assert_eq!(
        asked() - before_retry,
        reuse_cost,
        "the healthy host's client was kept"
    );
}

#[test]
fn a_stated_length_past_the_cap_refuses_before_the_body() {
    // States 2000 bytes and never sends one, holding the connection open.
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        let Ok((stream, _)) = listener.accept() else {
            return;
        };
        let mut reader = BufReader::new(stream.try_clone().expect("clone"));
        let mut line = String::new();
        while reader.read_line(&mut line).unwrap_or(0) > 0 && line != "\r\n" {
            line.clear();
        }
        let mut stream = stream;
        let _ = stream.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2000\r\n\r\n");
        thread::sleep(std::time::Duration::from_secs(30));
    });
    let mut fetcher = loopback_fetcher();
    let started = std::time::Instant::now();
    let error = fetcher
        .fetch(&format!("http://127.0.0.1:{port}/big"), 1000)
        .expect_err("stated past the cap");
    let io = error.downcast_ref::<IoError>().expect("an IoError");
    assert_eq!(io.kind, IoErrorKind::Cap, "{error}");
    assert!(
        started.elapsed() < std::time::Duration::from_secs(5),
        "refused on the stated length, not after waiting for the body"
    );
}
