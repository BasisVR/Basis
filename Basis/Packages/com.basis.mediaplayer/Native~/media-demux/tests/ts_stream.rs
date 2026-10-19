//! MPEG-TS demuxer behaviour over the committed fixtures, plus replay of
//! four known fuzz crash inputs.

use media_clock::{Generation, MediaTime};
use media_demux::{
    AudioCodec, ContainerKind, DemuxLimits, Demuxer, Format, MAX_NOTES, MemSource, StreamEvent,
    TsDemuxer, VideoCodec, sniff_container,
};

fn open(bytes: Vec<u8>) -> TsDemuxer {
    TsDemuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("ts open")
}

fn drain(demux: &mut TsDemuxer, cap: usize) -> Vec<StreamEvent> {
    let mut events = Vec::new();
    for _ in 0..cap {
        match demux.next_event().expect("no source errors on fixtures") {
            StreamEvent::Eos(_) => break,
            event => events.push(event),
        }
    }
    events
}

fn fixture(name: &str) -> Vec<u8> {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name);
    std::fs::read(path).expect("fixture readable")
}

const TS_PKT: usize = 188;
const PMT_PID: u16 = 0x0100;

/// One 188-byte packet carrying `payload` and nothing else (no adaptation
/// field). `pusi` is what says a PSI section starts at the pointer field.
fn ts_packet(pid: u16, pusi: bool, payload: &[u8]) -> Vec<u8> {
    assert!(payload.len() <= TS_PKT - 4, "payload spans packets");
    let mut pkt = vec![0xFFu8; TS_PKT];
    pkt[0] = 0x47;
    pkt[1] = if pusi { 0x40 } else { 0x00 } | ((pid >> 8) as u8 & 0x1F);
    pkt[2] = (pid & 0xFF) as u8;
    pkt[3] = 0x10; // payload only, continuity counter 0
    pkt[4..4 + payload.len()].copy_from_slice(payload);
    pkt
}

/// The CRC-32/MPEG-2 a PSI section carries in its last four bytes, over
/// everything ahead of the field itself. The demuxer runs the same
/// polynomial over the whole section and requires zero.
fn psi_crc(section: &[u8]) -> [u8; 4] {
    let mut crc: u32 = 0xFFFF_FFFF;
    for &byte in section {
        crc ^= u32::from(byte) << 24;
        for _ in 0..8 {
            crc = if crc & 0x8000_0000 != 0 {
                (crc << 1) ^ 0x04C1_1DB7
            } else {
                crc << 1
            };
        }
    }
    crc.to_be_bytes()
}

/// Rewrite a built section's CRC over its own stated extent. Every helper
/// that edits a section after building it has to call this, or the row it
/// feeds is refused for its CRC rather than for the shape it was built to
/// have, and would then pass while proving something else.
fn restamp_crc(payload: &mut [u8]) {
    // payload[0] is the pointer field, so the section starts at 1 and its
    // length bytes are payload[2..4].
    let section_len = (usize::from(payload[2] & 0x0F) << 8) | usize::from(payload[3]);
    let stated = 3 + section_len;
    assert!(
        stated >= 4 && stated < payload.len(),
        "the section does not fit the payload it was built in"
    );
    let crc = psi_crc(&payload[1..1 + stated - 4]);
    payload[1 + stated - 4..1 + stated].copy_from_slice(&crc);
}

/// A single-program PAT payload: pointer field, then the section.
fn pat_payload(pmt_pid: u16) -> Vec<u8> {
    let section_len: u16 = 13; // 5 header + one 4-byte program + 4 CRC
    let mut s = vec![0x00, 0xB0 | (section_len >> 8) as u8, section_len as u8];
    s.extend_from_slice(&[0x00, 0x01]); // transport_stream_id
    s.extend_from_slice(&[0xC1, 0x00, 0x00]); // version 0 current, section 0 of 0
    s.extend_from_slice(&1u16.to_be_bytes()); // program_number
    s.extend_from_slice(&(0xE000 | pmt_pid).to_be_bytes());
    s.extend_from_slice(&psi_crc(&s));
    let mut payload = vec![0x00]; // pointer_field
    payload.extend_from_slice(&s);
    payload
}

/// The same PAT, with `current_next_indicator` clear: the table that
/// becomes applicable later rather than the one in force.
fn next_pat_payload(pmt_pid: u16) -> Vec<u8> {
    let mut payload = pat_payload(pmt_pid);
    // payload[0] is the pointer field, so the version byte carrying the
    // indicator is payload[6], as in the PMT helper.
    payload[6] &= !0x01;
    restamp_crc(&mut payload);
    payload
}

/// A single-section PMT payload at `version` listing
/// `(stream_type, elementary_pid)`.
fn pmt_payload(version: u8, entries: &[(u8, u16)]) -> Vec<u8> {
    pmt_section_payload(version, 0, 0, entries)
}

/// The same as section `section` of `last`. A PMT may legitimately span
/// several sections, which are one table between them.
fn pmt_section_payload(version: u8, section: u8, last: u8, entries: &[(u8, u16)]) -> Vec<u8> {
    let mut es = Vec::new();
    for (stype, pid) in entries {
        es.push(*stype);
        es.extend_from_slice(&(0xE000 | *pid).to_be_bytes());
        es.extend_from_slice(&0xF000u16.to_be_bytes()); // ES_info_length 0
    }
    let section_len = 13 + es.len() as u16;
    let mut s = vec![0x02, 0xB0 | (section_len >> 8) as u8, section_len as u8];
    s.extend_from_slice(&1u16.to_be_bytes()); // program_number
    s.push(0xC0 | ((version & 0x1F) << 1) | 0x01); // version, current
    s.extend_from_slice(&[section, last]);
    s.extend_from_slice(&0xE101u16.to_be_bytes()); // PCR_PID
    s.extend_from_slice(&0xF000u16.to_be_bytes()); // program_info_length 0
    s.extend_from_slice(&es);
    s.extend_from_slice(&psi_crc(&s));
    let mut payload = vec![0x00];
    payload.extend_from_slice(&s);
    payload
}

/// The same single-section PMT, announced for `program` rather than for
/// the one the other helpers use. A PMT pid may legally carry more than
/// one program's tables, told apart only by this field.
fn pmt_payload_for(program: u16, version: u8, entries: &[(u8, u16)]) -> Vec<u8> {
    let mut payload = pmt_payload(version, entries);
    // payload[0] is the pointer field, so the section's program_number
    // is payload[4..6].
    payload[4..6].copy_from_slice(&program.to_be_bytes());
    restamp_crc(&mut payload);
    payload
}

/// A PMT payload whose stated `section_length` is too short to reach its
/// own program-info field, so the walk finds nothing in it.
fn short_pmt_payload(version: u8) -> Vec<u8> {
    let mut payload = pmt_payload(version, &[(0x06, 0x0200)]);
    // payload[0] is the pointer field, so the section's length bytes are
    // payload[2..4].
    payload[2] = 0xB0;
    payload[3] = 5;
    // Over the shortened extent, so the row is refused for the length it
    // states rather than for the CRC that statement moved.
    restamp_crc(&mut payload);
    payload
}

/// The same section, with `current_next_indicator` clear: the table that
/// becomes applicable later rather than the one in force.
fn next_pmt_payload(version: u8, entries: &[(u8, u16)]) -> Vec<u8> {
    let mut payload = pmt_payload(version, entries);
    // payload[0] is the pointer field, so the version byte carrying the
    // indicator is payload[6].
    payload[6] &= !0x01;
    restamp_crc(&mut payload);
    payload
}

/// A sound PMT with one byte of its entry flipped and its CRC left as it
/// was: the shape is intact and only the check says otherwise, which is
/// what a lossy transport delivers.
fn corrupt_pmt_payload(version: u8, entries: &[(u8, u16)]) -> Vec<u8> {
    let mut payload = pmt_payload(version, entries);
    // The entry sits immediately before the CRC; its elementary pid is
    // the seventh byte from the end.
    let n = payload.len();
    payload[n - 7] ^= 0x01;
    payload
}

/// A PMT whose one entry declares a descriptor block the section has no
/// room for, so the entry does not lie inside its own section.
fn overrun_pmt_payload(version: u8) -> Vec<u8> {
    let mut payload = pmt_payload(version, &[(0x06, 0x0200)]);
    // The entry sits immediately before the CRC, so its ES_info_length is
    // the sixth and fifth bytes from the end.
    let n = payload.len();
    payload[n - 6] = 0xFF;
    payload[n - 5] = 0xFF;
    restamp_crc(&mut payload);
    payload
}

/// A complete PMT whose entries stop two bytes short of the CRC, so the
/// last of the space they are supposed to tile holds no entry at all.
fn ragged_pmt_payload(version: u8) -> Vec<u8> {
    let mut payload = pmt_payload(version, &[(0x06, 0x0200)]);
    // One five-byte entry tiles a section of 18; stating 20 leaves two
    // bytes over, which is not another entry.
    payload[2] = 0xB0;
    payload[3] = 20;
    // The stated extent runs two bytes past what was built, and the
    // demuxer reads those from the packet's filler. Carry them here before
    // restamping, or the section fails its own CRC and is refused one
    // screen earlier than this row is about.
    payload.extend_from_slice(&[0xFF, 0xFF]);
    restamp_crc(&mut payload);
    payload
}

/// A PMT whose stated `section_length` runs past the packet carrying it,
/// which is what a table spanning several packets looks like to a parser
/// that reads one packet at a time.
fn spanning_pmt_payload(version: u8, entries: &[(u8, u16)]) -> Vec<u8> {
    let mut payload = pmt_payload(version, entries);
    // payload[0] is the pointer field, so the section's length bytes are
    // payload[2..4]. 300 bytes of section against 183 of packet.
    payload[2] = 0xB0 | 0x01;
    payload[3] = 0x2C;
    payload
}

/// Drain a synthetic stream and hand back the notes it accumulated.
fn notes_from(packets: Vec<Vec<u8>>) -> (TsDemuxer, Vec<String>) {
    let mut demux = open(packets.concat());
    drain(&mut demux, 100_000);
    let notes = demux.take_notes();
    (demux, notes)
}

/// The PMT PID carries the table for the whole stream, so a walk belongs
/// to a section this table has not shown yet rather than to a section
/// arriving. Two different contents under one section number at one
/// version is a stream contradicting itself; the first is what the
/// session holds.
#[test]
fn a_repeated_pmt_section_is_walked_once() {
    let (_demux, notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &pmt_payload(0, &[(0x06, 0x0200)])),
        ts_packet(PMT_PID, true, &pmt_payload(0, &[(0x07, 0x0201)])),
        ts_packet(PMT_PID, true, &pmt_payload(1, &[(0x08, 0x0202)])),
    ]);
    assert!(
        notes.iter().any(|n| n.contains("0x06")),
        "the first section is walked: {notes:?}"
    );
    assert!(
        !notes.iter().any(|n| n.contains("0x07")),
        "a repeat of the same section is not walked again: {notes:?}"
    );
    assert!(
        notes.iter().any(|n| n.contains("0x08")),
        "a version bump is walked: {notes:?}"
    );
}

/// A PAT that is not yet applicable may not repoint the PMT PID. It
/// names where the table in force lives, so acting on the next copy
/// stops the demuxer reading the PID that is in force, and because the
/// PID is the new table's identity, the sections already walked under
/// the old one go with it. The decoy PID here carries nothing, so a
/// stream that took it binds no tracks at all.
#[test]
fn a_pat_that_is_not_yet_applicable_does_not_repoint_the_pmt_pid() {
    const DECOY_PID: u16 = 0x0555;
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(0, true, &next_pat_payload(DECOY_PID)),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert!(
        demux.video_track().is_some(),
        "the PMT PID in force was still read for video"
    );
    assert!(
        demux.audio_track().is_some(),
        "the PMT PID in force was still read for audio"
    );
}

/// Whatever else arrives on PID 0 is not a PAT, and must not be able to
/// repoint the PMT PID by sitting on the right pid.
#[test]
fn a_section_on_pid_zero_that_is_not_a_pat_is_ignored() {
    const DECOY_PID: u16 = 0x0555;
    let mut impostor = pat_payload(DECOY_PID);
    impostor[1] = 0x02; // a PMT's table_id, on the PAT's pid
    restamp_crc(&mut impostor);
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(0, true, &impostor),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert!(
        demux.video_track().is_some(),
        "the impostor did not take the PAT's place"
    );
}

/// A section that arrived whole carries its own CRC, and one that fails
/// it must not be walked. Every claiming arm is guarded on the pid being
/// unbound and the section number is latched on the way, so a corrupt
/// copy acted on first both binds what it names and consumes the
/// identity; the sound copy repeating behind it is then skipped and
/// cannot replace either. Observed through the unclaimed-stream_type
/// note, which is the walk's only outward effect: the corrupt copy's
/// type must never appear and the sound copy's must.
#[test]
fn a_section_failing_its_crc_leaves_the_walk_to_the_sound_copy() {
    let (_demux, notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &corrupt_pmt_payload(0, &[(0x06, 0x0200)])),
        ts_packet(PMT_PID, true, &pmt_payload(0, &[(0x07, 0x0201)])),
    ]);
    assert!(
        !notes.iter().any(|n| n.contains("0x06")),
        "a section that fails its own check was walked: {notes:?}"
    );
    assert!(
        notes.iter().any(|n| n.contains("0x07")),
        "the sound copy behind it did not get the identity: {notes:?}"
    );
}

/// One PMT pid, two programs. The section header's program_number is the
/// only thing that tells their tables apart, so a demuxer reading the
/// pid alone binds whichever section arrives first. Both normally
/// arrive as version 0, section 0, so the second is then dropped as a
/// repeat of the first. Here the PAT selects program 1 and program 2's
/// section arrives ahead of it, so binding by arrival order takes the
/// wrong program's elementary streams and cannot be corrected after.
#[test]
fn a_pmt_for_another_program_on_the_same_pid_is_not_this_one() {
    const OTHER_PROGRAM: u16 = 2;
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload_for(OTHER_PROGRAM, 0, &[(0x1B, 0x0201), (0x0F, 0x0202)]),
        ),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert_eq!(
        demux.video_track().map(|t| t.0),
        Some(0x0101),
        "the selected program's video bound, not the one sharing its pid"
    );
    assert_eq!(
        demux.audio_track().map(|t| t.0),
        Some(0x0102),
        "the selected program's audio bound, not the one sharing its pid"
    );
}

/// The latch must not cost the stream its tracks: a program that gains
/// them in a later table still binds them.
#[test]
fn a_pmt_version_bump_binds_the_tracks_it_adds() {
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &pmt_payload(0, &[(0x06, 0x0200)])),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(1, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert!(
        demux.video_track().is_some(),
        "H.264 bound from the new table"
    );
    assert!(
        demux.audio_track().is_some(),
        "AAC bound from the new table"
    );
}

/// A PMT may legitimately span several sections, and the stream repeats
/// the whole cycle. Each section of a table is walked once, so a cycle
/// costs nothing after the first. Tracking only the previous section
/// would let an alternating pair thrash the latch forever, which is also
/// the cheapest way for a stream to defeat it on purpose.
#[test]
fn each_section_of_a_multi_section_pmt_is_walked_once() {
    let (_demux, notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(
            PMT_PID,
            true,
            &pmt_section_payload(0, 0, 1, &[(0x06, 0x0200)]),
        ),
        ts_packet(
            PMT_PID,
            true,
            &pmt_section_payload(0, 1, 1, &[(0x07, 0x0201)]),
        ),
        // The cycle repeats. Same version, same section numbers, different
        // contents, so anything walked here shows up as a note.
        ts_packet(
            PMT_PID,
            true,
            &pmt_section_payload(0, 0, 1, &[(0x08, 0x0202)]),
        ),
        ts_packet(
            PMT_PID,
            true,
            &pmt_section_payload(0, 1, 1, &[(0x09, 0x0203)]),
        ),
    ]);
    assert!(
        notes.iter().any(|n| n.contains("0x06")) && notes.iter().any(|n| n.contains("0x07")),
        "both sections of the table are walked: {notes:?}"
    );
    assert!(
        !notes.iter().any(|n| n.contains("0x08")) && !notes.iter().any(|n| n.contains("0x09")),
        "the repeat of the cycle is not walked again: {notes:?}"
    );
}

/// A table's identity includes the PID that carried it. A PAT that selects
/// a different PMT PID names a different table even though both are at the
/// usual version 0, section 0, so the tracks the new one names must still
/// bind.
#[test]
fn a_new_pmt_pid_is_a_new_table_at_the_same_section_key() {
    let (demux, notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &pmt_payload(0, &[(0x06, 0x0200)])),
        // The PAT re-points at another PID; its table is version 0
        // section 0 as well, which is the colliding case.
        ts_packet(0, true, &pat_payload(0x0101)),
        ts_packet(
            0x0101,
            true,
            &pmt_payload(0, &[(0x1B, 0x0111), (0x0F, 0x0112)]),
        ),
    ]);
    assert!(
        notes.iter().any(|n| n.contains("0x06")),
        "the first table was walked: {notes:?}"
    );
    assert!(
        demux.video_track().is_some(),
        "H.264 bound from the newly selected table"
    );
    assert!(
        demux.audio_track().is_some(),
        "AAC bound from the newly selected table"
    );
}

/// A section whose stated length leaves no room for its own entries is
/// walked over without reading anything, so it must not count as walked:
/// the sound copy that follows it carries the same PID, version and
/// section number, and its tracks have to bind.
#[test]
fn a_malformed_pmt_section_does_not_consume_a_real_ones_identity() {
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &short_pmt_payload(0)),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert!(
        demux.video_track().is_some(),
        "H.264 bound from the sound copy of the section"
    );
    assert!(
        demux.audio_track().is_some(),
        "AAC bound from the sound copy of the section"
    );
}

/// A section with `current_next_indicator` clear is the next table, not
/// the one in force. Every claiming arm is guarded on the pid being
/// unbound, so a next table walked first takes a binding the applicable
/// table can no longer replace, and the session plays whichever
/// elementary streams the not-yet-applicable copy happened to name.
#[test]
fn a_next_pmt_binds_nothing() {
    // A third packet because stride detection needs two packets of
    // lookahead past the sync byte: a two-packet stream never locks, and
    // the row would pass having parsed nothing at all.
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(
            PMT_PID,
            true,
            &next_pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
        ts_packet(0, true, &pat_payload(PMT_PID)),
    ]);
    assert!(demux.video_track().is_none(), "the next table bound video");
    assert!(demux.audio_track().is_none(), "the next table bound audio");
}

/// And it does not consume the identity either: the applicable copy of
/// the same version arrives behind it and is the one that binds.
#[test]
fn the_current_pmt_still_binds_behind_a_next_one() {
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(
            PMT_PID,
            true,
            &next_pmt_payload(0, &[(0x1B, 0x0301), (0x0F, 0x0302)]),
        ),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    // The pids, not merely something: the two tables name different ones,
    // so binding at all is not the same as binding the right table.
    assert_eq!(
        demux.video_track().map(|t| t.0),
        Some(0x0101),
        "video came from the applicable table"
    );
    assert_eq!(
        demux.audio_track().map(|t| t.0),
        Some(0x0102),
        "audio came from the applicable table"
    );
}

/// The header checks are not the whole of what makes a section sound: an
/// entry whose descriptor block runs past the CRC is walked as far as it
/// goes and abandoned. Taking the identity before the entries are known
/// to fit would discard the sound copy repeating behind it, and the
/// program would never bind for as long as that version stood.
#[test]
fn a_pmt_entry_outside_its_section_does_not_consume_the_identity() {
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &overrun_pmt_payload(0)),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert!(
        demux.video_track().is_some(),
        "H.264 bound from the sound copy of the section"
    );
    assert!(
        demux.audio_track().is_some(),
        "AAC bound from the sound copy of the section"
    );
}

/// A PMT too large for one packet is clamped to what the packet holds
/// rather than refused, and binds the tracks its first packet names;
/// refusing it instead would bind none of them. The entry check must not
/// turn that into a refusal: the entries beyond the clamp are not
/// malformed, they are simply not here.
#[test]
fn a_pmt_spanning_packets_still_binds_what_its_first_packet_names() {
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(
            PMT_PID,
            true,
            &spanning_pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
        ts_packet(0, true, &pat_payload(PMT_PID)),
    ]);
    assert_eq!(
        demux.video_track().map(|t| t.0),
        Some(0x0101),
        "the clamped table still binds the video it names"
    );
    assert_eq!(
        demux.audio_track().map(|t| t.0),
        Some(0x0102),
        "the clamped table still binds the audio it names"
    );
}

/// A remainder too short to be another entry is the same defect as an
/// entry running past the CRC (the section does not describe what it
/// says it describes), and it must not cost the sound copy behind it
/// either.
#[test]
fn a_pmt_with_a_ragged_tail_does_not_consume_the_identity() {
    let (demux, _notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &ragged_pmt_payload(0)),
        ts_packet(
            PMT_PID,
            true,
            &pmt_payload(0, &[(0x1B, 0x0101), (0x0F, 0x0102)]),
        ),
    ]);
    assert_eq!(
        demux.video_track().map(|t| t.0),
        Some(0x0101),
        "the sound copy of the section still binds"
    );
}

/// Once video and audio are bound every remaining entry is unclaimed, and
/// a PMT holds dozens of them per packet, so this is the note sink a stream
/// can drive hardest. It is capped, and the cap is what keeps the duplicate
/// scan from going quadratic in what the stream sent.
#[test]
fn unclaimed_stream_type_notes_are_capped() {
    let mut packets = vec![ts_packet(0, true, &pat_payload(PMT_PID))];
    // Each table bumps its version so the latch lets it through, and names
    // twenty entries no arm claims: 620 distinct notes offered.
    for version in 0..31u8 {
        let entries: Vec<(u8, u16)> = (0..20)
            .map(|i| (0x06, 0x0200 + u16::from(version) * 20 + i))
            .collect();
        packets.push(ts_packet(PMT_PID, true, &pmt_payload(version, &entries)));
    }
    let offered = 31 * 20;
    let (_demux, notes) = notes_from(packets.clone());
    assert!(offered > MAX_NOTES, "the row has to overrun the cap");
    assert_eq!(notes.len(), MAX_NOTES, "filled and stopped");
    let mut distinct = notes.clone();
    distinct.sort();
    distinct.dedup();
    assert_eq!(distinct.len(), notes.len(), "still deduplicated");

    // Boundedness, not merely smallness: five times the input, same size.
    let mut longer = packets.clone();
    for _ in 0..4 {
        longer.extend(packets.iter().skip(1).cloned());
    }
    let (_demux, more) = notes_from(longer);
    assert_eq!(more.len(), MAX_NOTES);
}

/// A continuation payload has no pointer field, so its bytes are not a
/// table header however much they look like one: the packet has to say a
/// unit starts there.
#[test]
fn a_pmt_continuation_payload_is_not_read_as_a_section() {
    let (_demux, notes) = notes_from(vec![
        ts_packet(0, true, &pat_payload(PMT_PID)),
        ts_packet(PMT_PID, true, &pmt_payload(0, &[(0x06, 0x0200)])),
        ts_packet(PMT_PID, false, &pmt_payload(1, &[(0x07, 0x0201)])),
    ]);
    assert!(
        notes.iter().any(|n| n.contains("0x06")),
        "the real section is walked: {notes:?}"
    );
    assert!(
        !notes.iter().any(|n| n.contains("0x07")),
        "a payload that would parse as a table is not one: {notes:?}"
    );
}

#[test]
fn demuxes_the_av_fixture() {
    let mut demux = open(fixture("h264-aac-640x360-30fps.ts"));
    let events = drain(&mut demux, 100_000);

    let mut video_format = None;
    let mut audio_format = None;
    let mut video_aus = 0usize;
    let mut audio_aus = 0usize;
    let mut keyframes = 0usize;
    for event in &events {
        match event {
            StreamEvent::Format(_, f @ Format::Video { .. }) => video_format = Some(f.clone()),
            StreamEvent::Format(_, f @ Format::Audio { .. }) => audio_format = Some(f.clone()),
            StreamEvent::Au(au) => {
                if Some(au.track) == demux.video_track() {
                    video_aus += 1;
                    if au.key {
                        keyframes += 1;
                    }
                } else {
                    audio_aus += 1;
                }
            }
            _ => {}
        }
    }

    // Counts pinned against ffprobe (the conformance gate checks the full
    // per-packet detail; this keeps a cheap in-tree signal).
    assert_eq!(video_aus, 180);
    assert_eq!(audio_aus, 283);
    assert_eq!(keyframes, 3); // GOP 60 over 180 frames
    let Some(Format::Video {
        codec,
        display_width,
        display_height,
        ..
    }) = video_format
    else {
        panic!("no video format announced");
    };
    assert_eq!(codec, VideoCodec::H264);
    assert_eq!((display_width, display_height), (640, 360));
    let Some(Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    }) = audio_format
    else {
        panic!("no audio format announced");
    };
    assert_eq!(codec, AudioCodec::Aac);
    assert_eq!(sample_rate, 48000);
    assert_eq!(channels, 2);
    assert_eq!(codec_private.len(), 2);
}

#[test]
fn m2ts_lpcm_announces_and_flows() {
    let mut demux = open(fixture("h264-lpcm-320x180.m2ts"));
    let events = drain(&mut demux, 100_000);
    let audio_format = events.iter().find_map(|e| match e {
        StreamEvent::Format(_, f @ Format::Audio { .. }) => Some(f.clone()),
        _ => None,
    });
    let Some(Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    }) = audio_format
    else {
        panic!("no LPCM format announced");
    };
    assert_eq!(codec, AudioCodec::Pcm);
    assert_eq!(sample_rate, 48000);
    assert_eq!(channels, 2);
    // [channel_assignment, bits_code, flags]: stereo is assignment 3,
    // 16-bit is 1, and flags bit 0 clear says the samples are big-endian.
    assert_eq!(codec_private, vec![3, 1, 0]);
    assert!(
        events
            .iter()
            .any(|e| matches!(e, StreamEvent::Au(au) if Some(au.track) == demux.audio_track()))
    );
}

#[test]
fn mid_gop_join_waits_for_an_sps_keyframe() {
    // Drop the first 40% of the A/V fixture so the demuxer joins mid-GOP:
    // no AU may be emitted before an SPS-bearing keyframe, and the format
    // must still announce real dimensions.
    let bytes = fixture("h264-aac-640x360-30fps.ts");
    let cut = (bytes.len() * 2 / 5 / 188) * 188 + 100; // deliberately misaligned
    let mut demux = open(bytes[cut..].to_vec());
    let events = drain(&mut demux, 100_000);

    let first_video_key = events.iter().find_map(|e| match e {
        StreamEvent::Au(au) if Some(au.track) == demux.video_track() => Some(au.key),
        _ => None,
    });
    assert_eq!(
        first_video_key,
        Some(true),
        "first video AU must be a keyframe"
    );
    assert!(events.iter().any(|e| matches!(
        e,
        StreamEvent::Format(
            _,
            Format::Video {
                display_width: 640,
                ..
            }
        )
    )));
}

#[test]
fn seek_is_unsupported() {
    let mut demux = open(fixture("h264-aac-640x360-30fps.ts"));
    assert!(demux.seek(MediaTime::from_secs(1), Generation(1)).is_err());
    assert_eq!(demux.duration(), None);
}

#[test]
fn sniffs_ts_and_m2ts() {
    assert_eq!(
        sniff_container(&fixture("h264-aac-640x360-30fps.ts")[..1024]),
        Some(ContainerKind::MpegTs)
    );
    assert_eq!(
        sniff_container(&fixture("h264-lpcm-320x180.m2ts")[..1024]),
        Some(ContainerKind::MpegTs)
    );
    assert_eq!(
        sniff_container(&fixture("h264-aac-640x360-30fps.mp4")[..1024]),
        Some(ContainerKind::Mp4)
    );
    assert_eq!(sniff_container(&[0u8; 1024]), None);
}

/// Known fuzz crash inputs: each must walk to EOS (or a typed error)
/// without panicking.
#[test]
fn replays_the_pinned_c_fuzz_crashes() {
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fuzz/corpus/ts_stream");
    let mut replayed = 0usize;
    for entry in std::fs::read_dir(dir).expect("seed corpus present") {
        let path = entry.expect("dir entry").path();
        let bytes = std::fs::read(&path).expect("seed readable");
        let mut demux = open(bytes);
        for _ in 0..100_000 {
            match demux.next_event() {
                Ok(StreamEvent::Eos(_)) | Err(_) => break,
                Ok(_) => {}
            }
        }
        replayed += 1;
    }
    assert!(replayed >= 4, "expected the four pinned crash inputs");
}
