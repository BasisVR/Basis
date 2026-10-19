//! The pull-based demuxer contract: the engine pulls, the demuxer
//! owns nothing downstream and is never re-entered.

use media_clock::{Generation, MediaTime};

use crate::{Artwork, AudioCodec, DemuxError, StreamEvent, TrackId};

/// One selectable audio track, as the container describes it. Containers
/// that carry a single audio track report none: a picker with one entry is
/// not a choice, and the engine's binding is unambiguous there anyway.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AudioTrackInfo {
    pub id: TrackId,
    /// ISO 639 code as the container states it; `None` when unstated or
    /// explicitly undetermined. This is what makes the picker a language
    /// picker rather than a list of numbers.
    pub language: Option<String>,
    /// Human-readable track name, where the container carries one.
    pub label: Option<String>,
    pub codec: AudioCodec,
    pub sample_rate: u32,
    pub channels: u32,
}

/// Choices made at open that the demuxer cannot revisit later. Audio track
/// selection lands here rather than as a mid-stream call: switching track
/// re-opens the session at the current position, so the demuxer only ever
/// needs to honour a choice once.
#[derive(Debug, Clone, Default)]
pub struct DemuxOptions {
    /// Index into [`Demuxer::audio_tracks`] to bind. Out of range, or a
    /// track that turns out to be undecodable, falls back to the first
    /// usable one with a note.
    pub audio_track: usize,
}

/// Parse-time caps drawn from the session budget: enforced inside
/// the demuxer, not around it.
#[derive(Debug, Clone)]
pub struct DemuxLimits {
    /// Ceiling on bytes the metadata parse may pull (boxes, sample tables).
    pub max_metadata_bytes: u64,
    /// Ceiling on a single compressed access unit, far above any real one,
    /// so a hostile size field cannot drive a huge allocation.
    pub max_au_bytes: u64,
}

impl Default for DemuxLimits {
    fn default() -> Self {
        Self {
            max_metadata_bytes: 64 * 1024 * 1024,
            max_au_bytes: 64 * 1024 * 1024,
        }
    }
}

pub trait Demuxer: Send {
    /// Pull the next event. Emits `Format` events first, then interleaved
    /// `Au`s in decode order; returns `Eos` at the end, and keeps returning
    /// it if pulled again.
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError>;

    /// Reposition to the keyframe-clean point at or before `target`, adopt
    /// `generation` for everything emitted from here, and return the actual
    /// position. Unseekable (live) demuxers return `Unsupported`.
    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError>;

    /// Total duration where the container states one.
    fn duration(&self) -> Option<MediaTime>;

    /// The selected video track, once known. TS demuxers learn it from the
    /// PMT, so it can be `None` until the first packets are pulled.
    fn video_track(&self) -> Option<TrackId> {
        None
    }

    /// The selected audio track, once known (see [`Self::video_track`]).
    fn audio_track(&self) -> Option<TrackId> {
        None
    }

    /// Every audio track a caller could bind instead of the current one,
    /// in container order, or empty where there is no choice to offer.
    fn audio_tracks(&self) -> Vec<AudioTrackInfo> {
        Vec::new()
    }

    /// Embedded cover art, where the container carries one and the
    /// container is one that can. Read once after open, like the duration:
    /// it is a property of the file, not of the stream.
    fn artwork(&self) -> Option<&Artwork> {
        None
    }

    /// Where the bound audio track's sound begins on a timeline that starts
    /// at zero, when the container says the track starts late. The gap
    /// before it plays as silence, and audio decoded ahead of it is encoder
    /// priming and is not played. Zero for a track that starts with the
    /// timeline, or a container that does not say. Read once after open.
    fn audio_start(&self) -> MediaTime {
        MediaTime::ZERO
    }

    /// Where the bound video track's pictures end on the same timeline,
    /// when the container says the track stops before its media does. A
    /// sample shown past it is still emitted where a picture shown before it
    /// depends on that sample, but a decoded picture at or after it is not
    /// shown.
    /// `None` where the media plays to its end. Read once after open.
    fn video_end(&self) -> Option<MediaTime> {
        None
    }

    /// Where the bound audio track's sound ends (see [`Self::video_end`]):
    /// decoded sound at or after it is encoder padding or a trimmed tail and
    /// is not played.
    fn audio_end(&self) -> Option<MediaTime> {
        None
    }

    /// Drain per-track findings (tracks left aside, fallbacks taken) for
    /// the engine to surface as diagnostics.
    fn take_notes(&mut self) -> Vec<String> {
        Vec::new()
    }

    /// Drain the tracks left out because nothing here can play them, each
    /// with its reason. Unlike a note, a refusal is put in front of the
    /// viewer: it is part of the source they asked for and will not get.
    fn take_refusals(&mut self) -> Vec<String> {
        Vec::new()
    }
}

/// Ceiling on a note collection whose entries come from the stream rather
/// than from a container's fixed structure. Notes are drained once, after
/// open, so nothing empties them again for the life of the session: a
/// source that can keep producing distinct ones would otherwise grow the
/// collection without bound on the demux thread.
pub const MAX_NOTES: usize = 64;

/// Record a note unless it duplicates one already held or the collection
/// is full. The note is built only when there is room for it, so a source
/// that generates them endlessly costs nothing once the cap is reached.
pub fn push_note(notes: &mut Vec<String>, note: impl FnOnce() -> String) {
    if notes.len() >= MAX_NOTES {
        return;
    }
    let note = note();
    if !notes.contains(&note) {
        notes.push(note);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    #[test]
    fn notes_fill_to_the_cap_and_stop() {
        let mut notes = Vec::new();
        let built = Cell::new(0usize);
        for i in 0..MAX_NOTES * 4 {
            push_note(&mut notes, || {
                built.set(built.get() + 1);
                format!("finding {i}")
            });
        }
        assert_eq!(notes.len(), MAX_NOTES);
        assert_eq!(
            built.get(),
            MAX_NOTES,
            "a full collection does not even build the note"
        );
    }

    #[test]
    fn duplicate_notes_are_recorded_once() {
        let mut notes = Vec::new();
        for _ in 0..100 {
            push_note(&mut notes, || "the same finding".to_string());
        }
        assert_eq!(notes, ["the same finding"]);
    }
}
