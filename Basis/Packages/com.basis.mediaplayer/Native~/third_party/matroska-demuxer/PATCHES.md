# Vendored matroska-demuxer 0.8.1

A copy of the `matroska-demuxer` crate (Zlib OR MIT OR Apache-2.0, see the
LICENSE files), applied through `[patch.crates-io]` in the workspace root. The
lockfile is dropped. The source matches the crates.io release apart from three
seek changes in `src/lib.rs` and the size ceilings:

- `seek_broad_phase`: `CueRelativePosition` is resolved against the data offset
  of the cluster the cue point names. The release resolves it against the first
  cluster, so every cue seek past the first cluster returns EOF or lands in the
  wrong place.
- `seek_broad_phase`, linear fallback: the cluster offsets passed to the narrow
  phase are element positions, as the cue path provides. The release passes
  data offsets, which the narrow phase cannot enter, and falls back to offset 0
  when the first cluster is already past the target.
- `seek_to_cue_point`, new: like `seek`, but when a cue point is used the narrow
  phase targets the cue's own time, so the reader lands on the keyframe at or
  before the target instead of inside a group of pictures. `seek` keeps the
  release's behaviour (first block at or after the target), which the crate's
  own tests cover; they pass here, bar the eight `parse_testN_mkv` tests whose
  files the published package does not ship.
- Size ceilings: a string or binary element larger than 16 MiB
  (`MAX_ELEMENT_SIZE`, `src/ebml.rs`) is refused before anything is
  allocated for it, and so is a frame larger than the size set with the new
  `MatroskaFile::set_max_frame_size`. Both report the new
  `DemuxError::ElementSizeExceedsLimit`. The release bounds them only by the
  stream length the reader reports, which over a network is the server's
  word, so a file of a few bytes stating a terabyte aborts the process on the
  allocation.

`MkvDemuxer` uses `seek_to_cue_point` and sets the frame ceiling to its
access-unit limit. Each change is a candidate for an upstream report or pull
request. The copy can go once a release carries them.
