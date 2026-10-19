//! WHEP signalling surfaces fed by a hostile server: the Link
//! header walker and scheme mapper (ours, hand-parsed) and str0m's SDP
//! offer/answer parsers (the POST response body is attacker-controlled;
//! a parser panic there is a DoS through any WHEP endpoint). Parse-only:
//! no Rtc is built, so no crypto provider is needed.

#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let Ok(text) = std::str::from_utf8(data) else {
        return;
    };
    let _ = media_whep::ice_servers_from_links([text].into_iter());
    let _ = media_whep::signalling_url(text);
    let _ = str0m::change::SdpOffer::from_sdp_string(text);
    let _ = str0m::change::SdpAnswer::from_sdp_string(text);
});
