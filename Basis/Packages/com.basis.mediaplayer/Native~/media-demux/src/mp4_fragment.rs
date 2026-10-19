//! Sample tables built one movie fragment at a time, from the rules in
//! ISO/IEC 14496-12: `trex` defaults (8.8.3), the `tfhd` overrides and
//! base data offset (8.8.7), the `tfdt` base decode time (8.8.12) and the
//! `trun` run table (8.8.8).
//!
//! A fragment is self-describing but for one thing: a track fragment with
//! no `tfdt` continues the decode timeline of the fragment before it, so
//! the caller carries a cursor per track and sets it after a seek.

use std::collections::BTreeMap;

use re_mp4::{MoofBox, MoovBox, TfhdBox, TrunBox};

/// What a track's fragments inherit from `moov` when they state nothing.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub(crate) struct TrackDefaults {
    pub timescale: u64,
    pub sample_duration: u32,
    pub sample_size: u32,
    pub sample_flags: u32,
}

/// One sample, as the fragment holding it states it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct FragmentSample {
    pub offset: u64,
    pub size: u32,
    pub dts: i64,
    pub pts: i64,
    pub duration: u32,
    pub sync: bool,
}

/// Where each track's decode timeline stands between fragments.
pub(crate) type Cursors = BTreeMap<u32, i64>;

/// `trex` defaults and timescales, by track. A fragmented file always has
/// `mvex`; a file without one has its samples in `moov` and never reaches
/// this module.
pub(crate) fn track_defaults(moov: &MoovBox) -> BTreeMap<u32, TrackDefaults> {
    let mut out = BTreeMap::new();
    for trak in &moov.traks {
        let trex = moov
            .mvex
            .as_ref()
            .and_then(|mvex| {
                mvex.trexs
                    .iter()
                    .find(|trex| trex.track_id == trak.tkhd.track_id)
            })
            .cloned()
            .unwrap_or_default();
        out.insert(
            trak.tkhd.track_id,
            TrackDefaults {
                timescale: u64::from(trak.mdia.mdhd.timescale),
                sample_duration: trex.default_sample_duration,
                sample_size: trex.default_sample_size,
                sample_flags: trex.default_sample_flags,
            },
        );
    }
    out
}

/// Every sample one movie fragment holds, per track, in the order the
/// fragment lists them. `cursors` is read for a track fragment that
/// states no `tfdt` and advanced past the samples either way.
///
/// `budget` is how many samples the caller will still take, and is spent
/// by each run's count before the run is read: a run carrying no
/// per-sample fields states its count in four bytes, so the box's size
/// bounds nothing.
pub(crate) fn build(
    moof: &MoofBox,
    defaults: &BTreeMap<u32, TrackDefaults>,
    cursors: &mut Cursors,
    budget: &mut usize,
) -> Result<Vec<(u32, Vec<FragmentSample>)>, &'static str> {
    let mut out: Vec<(u32, Vec<FragmentSample>)> = Vec::with_capacity(moof.trafs.len());
    // Where the data of the track fragment before this one ended: what a
    // `traf` past the first inherits as its base when it states neither a
    // base data offset nor that the base is the `moof`.
    let mut previous_end = moof.start;

    for (index, traf) in moof.trafs.iter().enumerate() {
        let track_id = traf.tfhd.track_id;
        // `tfhd` states this fragment's defaults over the track's (8.8.7).
        let track = defaults.get(&track_id).copied().unwrap_or_default();
        let defaults = TrackDefaults {
            sample_duration: traf
                .tfhd
                .default_sample_duration
                .unwrap_or(track.sample_duration),
            sample_size: traf.tfhd.default_sample_size.unwrap_or(track.sample_size),
            sample_flags: traf.tfhd.default_sample_flags.unwrap_or(track.sample_flags),
            ..track
        };
        let base = base_data_offset(&traf.tfhd, moof.start, index, previous_end);

        let mut dts = match &traf.tfdt {
            Some(tfdt) => tfdt.base_media_decode_time.cast_signed(),
            None => cursors.get(&track_id).copied().unwrap_or(0),
        };
        let mut position = base;
        let mut samples = Vec::new();

        for trun in &traf.truns {
            if trun.flags & TrunBox::FLAG_DATA_OFFSET != 0 {
                position = base
                    .checked_add_signed(i64::from(trun.data_offset.unwrap_or(0)))
                    .ok_or("a run's data offset falls outside the file")?;
            }
            let count = trun.sample_count as usize;
            *budget = budget
                .checked_sub(count)
                .ok_or("a fragment states more samples than the demuxer holds")?;
            for i in 0..count {
                let size = pick(
                    &trun.sample_sizes,
                    i,
                    trun.flags & TrunBox::FLAG_SAMPLE_SIZE,
                    defaults.sample_size,
                );
                let duration = pick(
                    &trun.sample_durations,
                    i,
                    trun.flags & TrunBox::FLAG_SAMPLE_DURATION,
                    defaults.sample_duration,
                );
                let flags = sample_flags(trun, i, defaults.sample_flags);
                // Version 1 makes the composition offset signed, which is
                // how a run of B-frames states a picture presented before
                // the one decoded ahead of it.
                let cts = if trun.flags & TrunBox::FLAG_SAMPLE_CTS == 0 {
                    0
                } else {
                    let raw = trun.sample_cts.get(i).copied().unwrap_or(0);
                    if trun.version == 1 {
                        i64::from(raw.cast_signed())
                    } else {
                        i64::from(raw)
                    }
                };

                samples.push(FragmentSample {
                    offset: position,
                    size,
                    dts,
                    pts: dts
                        .checked_add(cts)
                        .ok_or("a sample's composition time runs past the end of time")?,
                    duration,
                    // Bit 16 of the sample flags is
                    // `sample_is_non_sync_sample` (8.8.3.1), so a sync
                    // sample is one where it is clear.
                    sync: (flags >> 16) & 1 == 0,
                });

                position = position
                    .checked_add(u64::from(size))
                    .ok_or("a sample runs past the end of the file")?;
                dts = dts
                    .checked_add(i64::from(duration))
                    .ok_or("a sample's decode time runs past the end of time")?;
            }
        }

        previous_end = position;
        cursors.insert(track_id, dts);
        out.push((track_id, samples));
    }
    Ok(out)
}

/// The byte the track fragment's data is measured from (8.8.7).
fn base_data_offset(tfhd: &TfhdBox, moof_start: u64, index: usize, previous_end: u64) -> u64 {
    if tfhd.flags & TfhdBox::FLAG_BASE_DATA_OFFSET != 0 {
        tfhd.base_data_offset.unwrap_or(moof_start)
    } else if tfhd.flags & TfhdBox::FLAG_DEFAULT_BASE_IS_MOOF != 0 || index == 0 {
        moof_start
    } else {
        previous_end
    }
}

/// A run's per-sample value where it carries one, the track default
/// otherwise.
fn pick(values: &[u32], i: usize, present: u32, default: u32) -> u32 {
    if present == 0 {
        default
    } else {
        values.get(i).copied().unwrap_or(default)
    }
}

/// Per-sample flags, the run's first-sample override, or the default.
fn sample_flags(trun: &TrunBox, i: usize, default: u32) -> u32 {
    if trun.flags & TrunBox::FLAG_SAMPLE_FLAGS != 0 {
        trun.sample_flags.get(i).copied().unwrap_or(default)
    } else if i == 0 && trun.flags & TrunBox::FLAG_FIRST_SAMPLE_FLAGS != 0 {
        trun.first_sample_flags.unwrap_or(default)
    } else {
        default
    }
}

#[cfg(test)]
mod tests {
    use re_mp4::TrafBox;

    use super::*;

    fn build_unbounded(
        moof: &MoofBox,
        defaults: &BTreeMap<u32, TrackDefaults>,
        cursors: &mut Cursors,
    ) -> Result<Vec<(u32, Vec<FragmentSample>)>, &'static str> {
        let mut budget = usize::MAX;
        build(moof, defaults, cursors, &mut budget)
    }

    fn fixture(name: &str) -> Vec<u8> {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../fixtures")
            .join(name);
        std::fs::read(path).expect("fixture readable")
    }

    /// Every committed fragmented fixture, built fragment by fragment,
    /// must come out sample for sample as the whole-file parse has it:
    /// same byte range, same decode and composition times, same sync
    /// flags, in the same order.
    #[test]
    fn a_fragment_at_a_time_matches_the_whole_file_parse() {
        for name in [
            "h264-aac-frag.mp4",
            "h264-aac-manyfrag.mp4",
            "h264-aac-manyfrag-sidx.mp4",
        ] {
            let bytes = fixture(name);
            let mp4 = re_mp4::Mp4::read_bytes(&bytes).expect("fixture parses");
            let defaults = track_defaults(&mp4.moov);
            assert!(!mp4.moofs.is_empty(), "{name} is fragmented");

            let mut cursors = Cursors::new();
            let mut built: BTreeMap<u32, Vec<FragmentSample>> = BTreeMap::new();
            for moof in &mp4.moofs {
                for (track_id, samples) in build_unbounded(moof, &defaults, &mut cursors)
                    .expect("a fixture's fragment builds")
                {
                    built.entry(track_id).or_default().extend(samples);
                }
            }

            for (track_id, track) in mp4.tracks() {
                let ours = built.get(track_id).unwrap_or_else(|| {
                    panic!("{name}: track {track_id} has no fragments");
                });
                assert_eq!(ours.len(), track.samples.len(), "{name} track {track_id}");
                for (i, (ours, theirs)) in ours.iter().zip(&track.samples).enumerate() {
                    assert_eq!(
                        (
                            ours.offset,
                            u64::from(ours.size),
                            ours.dts,
                            ours.pts,
                            ours.sync
                        ),
                        (
                            theirs.offset,
                            theirs.size,
                            theirs.decode_timestamp,
                            theirs.composition_timestamp,
                            theirs.is_sync
                        ),
                        "{name} track {track_id} sample {i}"
                    );
                }
            }
        }
    }

    /// The arms the committed fixtures never take: a run stating signed
    /// composition offsets, a first-sample flag that makes one sample of
    /// a run a sync point, a track fragment measured from the end of the
    /// one before it, and one with no data offset at all.
    #[test]
    fn a_fragment_states_its_own_offsets_times_and_sync_points() {
        const NON_SYNC: u32 = 1 << 16;
        let mut moof = MoofBox {
            start: 1000,
            ..MoofBox::default()
        };
        moof.trafs.push(TrafBox {
            tfhd: TfhdBox {
                track_id: 1,
                flags: TfhdBox::FLAG_DEFAULT_BASE_IS_MOOF
                    | TfhdBox::FLAG_DEFAULT_SAMPLE_DURATION
                    | TfhdBox::FLAG_DEFAULT_SAMPLE_FLAGS,
                default_sample_duration: Some(10),
                default_sample_flags: Some(NON_SYNC),
                ..TfhdBox::default()
            },
            tfdt: Some(re_mp4::TfdtBox {
                base_media_decode_time: 100,
                ..re_mp4::TfdtBox::default()
            }),
            truns: vec![TrunBox {
                version: 1,
                flags: TrunBox::FLAG_DATA_OFFSET
                    | TrunBox::FLAG_FIRST_SAMPLE_FLAGS
                    | TrunBox::FLAG_SAMPLE_SIZE
                    | TrunBox::FLAG_SAMPLE_CTS,
                sample_count: 3,
                data_offset: Some(200),
                first_sample_flags: Some(0),
                sample_sizes: vec![10, 20, 30],
                sample_cts: vec![0, (-5i32).cast_unsigned(), 5],
                ..TrunBox::default()
            }],
        });
        moof.trafs.push(TrafBox {
            tfhd: TfhdBox {
                track_id: 2,
                flags: TfhdBox::FLAG_DEFAULT_SAMPLE_DURATION,
                default_sample_duration: Some(3),
                ..TfhdBox::default()
            },
            tfdt: None,
            truns: vec![TrunBox {
                flags: TrunBox::FLAG_SAMPLE_SIZE,
                sample_count: 2,
                sample_sizes: vec![7, 8],
                ..TrunBox::default()
            }],
        });

        let mut cursors = Cursors::new();
        let built = build_unbounded(&moof, &BTreeMap::new(), &mut cursors).expect("builds");
        let sample = |offset, size, dts, pts, duration, sync| FragmentSample {
            offset,
            size,
            dts,
            pts,
            duration,
            sync,
        };
        assert_eq!(
            built[0],
            (
                1,
                vec![
                    sample(1200, 10, 100, 100, 10, true),
                    sample(1210, 20, 110, 105, 10, false),
                    sample(1230, 30, 120, 125, 10, false),
                ]
            )
        );
        // The second fragment states no base, so its data begins where
        // the first fragment's ended, and its decode time at zero.
        assert_eq!(
            built[1],
            (
                2,
                vec![
                    sample(1260, 7, 0, 0, 3, true),
                    sample(1267, 8, 3, 3, 3, true),
                ]
            )
        );
        assert_eq!(cursors[&1], 130);
        assert_eq!(cursors[&2], 6);
    }

    /// The timeline continues across a fragment that states no `tfdt`,
    /// and a `tfdt` overrides wherever the cursor stood. That is what makes
    /// a fragment reachable by a seek rather than only in order.
    #[test]
    fn a_fragment_without_a_base_time_continues_the_one_before_it() {
        let bytes = fixture("h264-aac-manyfrag.mp4");
        let mp4 = re_mp4::Mp4::read_bytes(&bytes).expect("fixture parses");
        let defaults = track_defaults(&mp4.moov);
        let video = mp4
            .tracks()
            .iter()
            .find(|(_, track)| track.kind == Some(re_mp4::TrackKind::Video))
            .map(|(id, _)| *id)
            .expect("a video track");

        // The third fragment built on its own, its base time believed.
        let moof = &mp4.moofs[2];
        let mut cursors = Cursors::new();
        let seeked = build_unbounded(moof, &defaults, &mut cursors).expect("builds");
        let from_index = seeked
            .iter()
            .find(|(id, _)| *id == video)
            .expect("video in the fragment")
            .1
            .clone();
        assert!(!from_index.is_empty());

        // The same fragment reached in order: the cursor it leaves is the
        // one the next fragment would start from.
        let mut in_order = Cursors::new();
        for moof in &mp4.moofs[..3] {
            build_unbounded(moof, &defaults, &mut in_order).expect("builds");
        }
        assert_eq!(cursors[&video], in_order[&video]);

        // With no base time and no cursor the run starts from zero, so
        // the `tfdt` is what places it.
        let base = moof
            .trafs
            .iter()
            .find(|traf| traf.tfhd.track_id == video)
            .and_then(|traf| traf.tfdt.as_ref())
            .expect("the fixture states a base time")
            .base_media_decode_time;
        assert_eq!(from_index[0].dts, base.cast_signed());
        assert!(base > 0, "the third fragment is not at the start");
    }

    /// A run with no per-sample fields takes every value from the
    /// defaults, so its count is all it states. The count is refused
    /// against the caller's budget before a sample is made, and the
    /// budget is spent across runs and track fragments alike.
    #[test]
    fn a_run_stating_more_samples_than_the_budget_is_refused() {
        let run = |sample_count| TrunBox {
            sample_count,
            ..TrunBox::default()
        };
        let traf = |track_id, truns| TrafBox {
            tfhd: TfhdBox {
                track_id,
                ..TfhdBox::default()
            },
            tfdt: None,
            truns,
        };

        let mut moof = MoofBox::default();
        moof.trafs.push(traf(1, vec![run(400), run(400)]));
        moof.trafs.push(traf(2, vec![run(200)]));
        let mut budget = 1000;
        let built = build(&moof, &BTreeMap::new(), &mut Cursors::new(), &mut budget)
            .expect("exactly the budget builds");
        assert_eq!((built[0].1.len(), built[1].1.len(), budget), (800, 200, 0));
        let mut budget = 999;
        assert!(build(&moof, &BTreeMap::new(), &mut Cursors::new(), &mut budget).is_err());

        let mut moof = MoofBox::default();
        moof.trafs.push(traf(1, vec![run(u32::MAX)]));
        let mut budget = 1000;
        assert!(build(&moof, &BTreeMap::new(), &mut Cursors::new(), &mut budget).is_err());
    }
}
