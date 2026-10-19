//! The address gate: the one implementation of the SSRF blocklist.
//! Policy above it (consent, per-world allowlists) lives managed-side; the
//! mechanism lives here and is consulted for every resolved address and
//! every redirect hop.

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};

pub trait AddressGate: Send + Sync {
    fn permit(&self, ip: IpAddr) -> bool;
}

/// Default gate: publicly routable addresses only.
pub struct PublicAddressGate;

impl AddressGate for PublicAddressGate {
    fn permit(&self, ip: IpAddr) -> bool {
        match ip {
            IpAddr::V4(v4) => permit_v4(v4),
            IpAddr::V6(v6) => permit_v6(v6),
        }
    }
}

fn permit_v4(ip: Ipv4Addr) -> bool {
    let [a, b, ..] = ip.octets();
    !(ip.is_unspecified()
        || ip.is_loopback()
        || ip.is_private()
        || ip.is_link_local()
        || ip.is_broadcast()
        || ip.is_documentation()
        || a == 0
        // CGNAT 100.64.0.0/10.
        || (a == 100 && (64..128).contains(&b))
        // IETF protocol assignments 192.0.0.0/24.
        || (a == 192 && b == 0 && ip.octets()[2] == 0)
        // Benchmarking 198.18.0.0/15.
        || (a == 198 && (b == 18 || b == 19))
        // Multicast + reserved 224.0.0.0/3.
        || a >= 224)
}

fn permit_v6(ip: Ipv6Addr) -> bool {
    // Addresses that embed a v4 address are gated on the embedded address.
    if let Some(v4) = ip.to_ipv4_mapped() {
        return permit_v4(v4);
    }
    let segments = ip.segments();
    // NAT64 well-known prefix 64:ff9b::/96.
    if segments[0] == 0x64 && segments[1] == 0xff9b {
        let [.., g, h] = segments;
        return permit_v4(Ipv4Addr::new(
            (g >> 8) as u8,
            g as u8,
            (h >> 8) as u8,
            h as u8,
        ));
    }
    !(ip.is_unspecified()
        || ip.is_loopback()
        // Unique-local fc00::/7.
        || (segments[0] & 0xfe00) == 0xfc00
        // Link-local fe80::/10.
        || (segments[0] & 0xffc0) == 0xfe80
        // Multicast ff00::/8.
        || (segments[0] & 0xff00) == 0xff00
        // Documentation 2001:db8::/32.
        || (segments[0] == 0x2001 && segments[1] == 0xdb8))
}

/// Explicit opt-out for local fixtures and the test rig. Never a default.
pub struct AllowAllGate;

impl AddressGate for AllowAllGate {
    fn permit(&self, _ip: IpAddr) -> bool {
        true
    }
}

/// Resolve a bare host and vet every returned address, returning the first,
/// for transports whose clients do their own dialling and take one address
/// (librist). A mixed public/private answer is the rebinding shape and is
/// refused whole. The client dials the returned literal rather than
/// resolving the hostname again, the same TOCTOU close as the pinned-IP HTTP
/// connect.
pub fn resolve_vetted(
    host: &str,
    port: u16,
    gate: &dyn AddressGate,
) -> Result<std::net::SocketAddr, crate::IoError> {
    match literal_vetted(host, port, gate)? {
        Some(addr) => Ok(addr),
        None => {
            let addrs = crate::resolve::resolve_blocking(host, port)?;
            first_vetted(host, addrs, gate)
        }
    }
}

/// [`resolve_vetted`], returning the whole answer in resolver order for a
/// client that tries each address in turn until one connects (RTSP).
pub fn resolve_vetted_all(
    host: &str,
    port: u16,
    gate: &dyn AddressGate,
) -> Result<Vec<std::net::SocketAddr>, crate::IoError> {
    match literal_vetted(host, port, gate)? {
        Some(addr) => Ok(vec![addr]),
        None => all_vetted(host, crate::resolve::resolve_blocking(host, port)?, gate),
    }
}

/// [`resolve_vetted`] for callers already in an async context: the
/// resolve is a real await point, so a `select!` racing this against a
/// cancel token can observe the cancel.
pub async fn resolve_vetted_async(
    host: &str,
    port: u16,
    gate: &dyn AddressGate,
) -> Result<std::net::SocketAddr, crate::IoError> {
    match literal_vetted(host, port, gate)? {
        Some(addr) => Ok(addr),
        None => {
            let addrs = crate::resolve::resolve_async(host, port).await?;
            first_vetted(host, addrs, gate)
        }
    }
}

/// `Some` when the host needed no lookup at all; the literal is settled
/// against the gate here rather than by the caller.
fn literal_vetted(
    host: &str,
    port: u16,
    gate: &dyn AddressGate,
) -> Result<Option<std::net::SocketAddr>, crate::IoError> {
    // A URL's host is the serialised form, so an IPv6 literal arrives
    // bracketed. Unstripped it would not parse and would go to the
    // resolver, which has no name to look up.
    let literal = host
        .strip_prefix('[')
        .and_then(|rest| rest.strip_suffix(']'))
        .unwrap_or(host);
    let Ok(ip) = literal.parse::<std::net::IpAddr>() else {
        return Ok(None);
    };
    if !gate.permit(ip) {
        return Err(crate::IoError::new(
            crate::IoErrorKind::Blocked,
            format!("{ip} blocked"),
        ));
    }
    Ok(Some(std::net::SocketAddr::new(ip, port)))
}

fn first_vetted(
    host: &str,
    addrs: Vec<std::net::SocketAddr>,
    gate: &dyn AddressGate,
) -> Result<std::net::SocketAddr, crate::IoError> {
    all_vetted(host, addrs, gate).map(|addrs| addrs[0])
}

fn all_vetted(
    host: &str,
    addrs: Vec<std::net::SocketAddr>,
    gate: &dyn AddressGate,
) -> Result<Vec<std::net::SocketAddr>, crate::IoError> {
    if addrs.is_empty() {
        return Err(crate::IoError::new(
            crate::IoErrorKind::Resolve,
            format!("{host}: no addresses"),
        ));
    }
    for addr in &addrs {
        if !gate.permit(addr.ip()) {
            return Err(crate::IoError::new(
                crate::IoErrorKind::Blocked,
                format!("{host} resolves to blocked {}", addr.ip()),
            ));
        }
    }
    Ok(addrs)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn blocked(s: &str) -> bool {
        !PublicAddressGate.permit(s.parse().unwrap())
    }

    #[test]
    fn private_and_special_ranges_are_blocked() {
        for addr in [
            "127.0.0.1",
            "10.0.0.1",
            "172.16.0.1",
            "172.31.255.255",
            "192.168.1.1",
            "169.254.169.254",
            "100.64.0.1",
            "100.127.255.255",
            "0.0.0.0",
            "0.1.2.3",
            "192.0.0.1",
            "198.18.0.1",
            "224.0.0.1",
            "240.0.0.1",
            "255.255.255.255",
            "::1",
            "::",
            "fe80::1",
            "fc00::1",
            "fd12:3456::1",
            "ff02::1",
            "::ffff:127.0.0.1",
            "::ffff:10.0.0.1",
            "64:ff9b::7f00:1",
            "2001:db8::1",
        ] {
            assert!(blocked(addr), "{addr} must be blocked");
        }
    }

    /// `Url::host_str` serialises an IPv6 host with its brackets, which is
    /// what every caller hands the gate. Unbracketed it parses as nothing,
    /// and a literal that is not recognised as one is sent to a resolver
    /// that has no name to resolve.
    #[test]
    fn a_bracketed_ipv6_literal_is_still_a_literal() {
        let addr = literal_vetted("[2606:4700:4700::1111]", 443, &AllowAllGate)
            .expect("a public literal is permitted")
            .expect("a bracketed literal is a literal");
        assert_eq!(addr.port(), 443);
        assert_eq!(
            addr.ip(),
            "2606:4700:4700::1111".parse::<std::net::IpAddr>().unwrap()
        );

        // And settled against the gate here rather than passed on.
        assert!(
            literal_vetted("[::1]", 443, &PublicAddressGate).is_err(),
            "the loopback literal is refused rather than resolved"
        );

        // A bracket that does not wrap the whole host is a name, and must
        // not be trimmed into a literal.
        assert!(
            matches!(literal_vetted("[::1", 443, &PublicAddressGate), Ok(None)),
            "an unclosed bracket is a name, not a literal"
        );
    }

    #[test]
    fn public_addresses_pass() {
        for addr in [
            "1.1.1.1",
            "8.8.8.8",
            "93.184.216.34",
            "100.128.0.1",
            "172.32.0.1",
            "2606:4700:4700::1111",
            "::ffff:8.8.8.8",
            "64:ff9b::808:808",
        ] {
            assert!(!blocked(addr), "{addr} must pass");
        }
    }
}
