use std::collections::BTreeMap;
use std::io::{Read, Seek};

use crate::{
    skip_box, BoxHeader, BoxType, EmsgBox, Error, FtypBox, MoofBox, MoovBox, ReadBox as _, Result,
    StblBox, StsdBoxContent, TfhdBox, TrackId, TrackKind, TrakBox, TrunBox,
};

#[derive(Debug)]
pub struct Mp4 {
    pub ftyp: FtypBox,
    pub moov: MoovBox,
    pub moofs: Vec<MoofBox>,
    pub emsgs: Vec<EmsgBox>,
    tracks: BTreeMap<TrackId, Track>,
}

impl Mp4 {
    /// Parses the contents of a byte slice as MP4 data.
    ///
    /// Sample ranges returned by the resulting [`Mp4`] should be used with the same input buffer.
    pub fn read_bytes(bytes: &[u8]) -> Result<Self> {
        let mp4 = Self::read(std::io::Cursor::new(bytes), bytes.len() as u64)?;
        Ok(mp4)
    }

    /// Reads the contents of a file as MP4 data, and returns both the parsed MP4 and its raw data.
    ///
    /// Sample ranges returned by the resulting [`Mp4`] should be used with the same input buffer.
    #[cfg(not(target_family = "wasm"))]
    pub fn read_file(file_path: impl AsRef<std::path::Path>) -> Result<(Self, Vec<u8>)> {
        let bytes = std::fs::read(file_path)?;
        Ok((Self::read_bytes(&bytes)?, bytes))
    }

    pub fn read<R: Read + Seek>(mut reader: R, size: u64) -> Result<Self> {
        let start = reader.stream_position()?;

        let mut ftyp = None;
        let mut moov = None;
        let mut moofs = Vec::new();
        let mut moof_offsets = Vec::new();
        let mut emsgs = Vec::new();

        let mut current = start;
        while current < size {
            // Get box header.
            let header = BoxHeader::read(&mut reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "file contains a box with a larger size than it",
                ));
            }

            // Break if size zero BoxHeader, which can result in dead-loop.
            if s == 0 {
                break;
            }

            // Match and parse the atom boxes.
            match name {
                BoxType::FtypBox => {
                    ftyp = Some(FtypBox::read_box(&mut reader, s)?);
                }
                BoxType::FreeBox | BoxType::MdatBox => {
                    skip_box(&mut reader, s)?;
                }
                BoxType::MoovBox => {
                    moov = Some(MoovBox::read_box(&mut reader, s)?);
                }
                BoxType::MoofBox => {
                    let moof_offset = reader.stream_position()? - 8;
                    let moof = MoofBox::read_box(&mut reader, s)?;
                    moofs.push(moof);
                    moof_offsets.push(moof_offset);
                }
                BoxType::EmsgBox => {
                    let emsg = EmsgBox::read_box(&mut reader, s)?;
                    emsgs.push(emsg);
                }
                _ => {
                    // XXX warn!()
                    skip_box(&mut reader, s)?;
                }
            }
            current = reader.stream_position()?;
        }

        let Some(ftyp) = ftyp else {
            return Err(Error::BoxNotFound(BoxType::FtypBox));
        };
        let Some(moov) = moov else {
            return Err(Error::BoxNotFound(BoxType::MoovBox));
        };

        let mut this = Self {
            ftyp,
            moov,
            moofs,
            emsgs,
            tracks: Default::default(),
        };

        let mut tracks = this.build_tracks();
        this.update_sample_list(&mut tracks)?;
        this.tracks = tracks;
        this.update_tracks();

        Ok(this)
    }

    pub fn tracks(&self) -> &BTreeMap<TrackId, Track> {
        &self.tracks
    }

    /// Process each `trak` box to obtain a list of samples for each track.
    ///
    /// Note that the list will be incomplete if the file is fragmented.
    fn build_tracks(&self) -> BTreeMap<TrackId, Track> {
        let mut tracks = BTreeMap::new();

        // load samples from traks
        for trak in &self.moov.traks {
            let mut sample_n = 0usize;
            let mut chunk_index = 1u64;
            let mut chunk_run_index = 0usize;
            let mut last_sample_in_chunk = 0u64;
            let mut offset_in_chunk = 0u64;
            let mut last_chunk_in_run = 0u64;
            let mut last_sample_in_stts_run = -1i64;
            let mut stts_run_index = -1i64;
            let mut last_stss_index = 0;
            let mut last_sample_in_ctts_run = -1i64;
            let mut ctts_run_index = -1i64;
            let mut dts_shift = 0;

            // The smallest presentation timestamp observed in this stream.
            //
            // This is typically 0, but in the presence of sample reordering (caused by AVC/HVC b-frames), it may be non-zero.
            // In fact, many formats don't require this to be zero, but video players typically
            // normalize the shown time to start at zero.
            // This is roughly equivalent to FFmpeg's internal `min_corrected_pts`
            // https://github.com/FFmpeg/FFmpeg/blob/4047b887fc44b110bccb1da09bcb79d6e454b88b/libavformat/isom.h#L202
            // To learn more about this I recommend reading the patch that introduced this in FFmpeg:
            // https://patchwork.ffmpeg.org/project/ffmpeg/patch/20170606181601.25187-1-isasi@google.com/#12592
            let mut min_composition_timestamp = i64::MAX;

            let mut samples = Vec::<Sample>::new();

            fn get_sample_chunk_offset(stbl: &StblBox, chunk_index: u64) -> u64 {
                if let Some(stco) = &stbl.stco {
                    stco.entries[chunk_index as usize - 1] as u64
                } else if let Some(co64) = &stbl.co64 {
                    co64.entries[chunk_index as usize - 1]
                } else {
                    panic!()
                }
            }

            let stbl = &trak.mdia.minf.stbl;
            let stsc = &stbl.stsc;
            let stsz = &stbl.stsz;
            let stts = &stbl.stts;

            // Could probably just always use sample count
            while (sample_n < stsz.sample_sizes.len() && stsz.sample_size == 0)
                || sample_n < stsz.sample_count as usize
            {
                // compute offset
                if sample_n == 0 {
                    chunk_index = 1;
                    chunk_run_index = 0;
                    last_sample_in_chunk = stsc.entries[chunk_run_index].samples_per_chunk as u64;
                    offset_in_chunk = 0;

                    if chunk_run_index + 1 < stsc.entries.len() {
                        last_chunk_in_run =
                            stsc.entries[chunk_run_index + 1].first_chunk as u64 - 1;
                    } else {
                        last_chunk_in_run = u64::MAX;
                    }
                } else if sample_n < last_sample_in_chunk as usize {
                    /* ... */
                } else {
                    chunk_index += 1;
                    offset_in_chunk = 0;
                    if chunk_index > last_chunk_in_run {
                        chunk_run_index += 1;
                        if chunk_run_index + 1 < stsc.entries.len() {
                            last_chunk_in_run =
                                stsc.entries[chunk_run_index + 1].first_chunk as u64 - 1;
                        } else {
                            last_chunk_in_run = u64::MAX;
                        }
                    }

                    last_sample_in_chunk += stsc.entries[chunk_run_index].samples_per_chunk as u64;
                }

                // compute timestamp, duration, is_sync
                let sample_n_i64 = i64::try_from(sample_n).unwrap_or(i64::MAX);
                if sample_n_i64 > last_sample_in_stts_run {
                    stts_run_index += 1;
                    if last_sample_in_stts_run < 0 {
                        last_sample_in_stts_run = 0;
                    }
                    last_sample_in_stts_run +=
                        stts.entries[stts_run_index as usize].sample_count as i64;
                }

                let timescale = trak.mdia.mdhd.timescale as u64;
                let size = if stsz.sample_size == 0 {
                    stsz.sample_sizes[sample_n] as u64
                } else {
                    stsz.sample_size as u64
                };
                let offset = get_sample_chunk_offset(stbl, chunk_index) + offset_in_chunk;
                offset_in_chunk += size;

                let decode_timestamp = if sample_n > 0 {
                    samples[sample_n - 1].duration =
                        stts.entries[stts_run_index as usize].sample_delta as u64;

                    samples[sample_n - 1].decode_timestamp
                        + samples[sample_n - 1].duration.cast_signed()
                } else {
                    0
                };

                let composition_timestamp = if let Some(ctts) = &stbl.ctts {
                    if sample_n_i64 >= last_sample_in_ctts_run {
                        ctts_run_index += 1;
                        if last_sample_in_ctts_run < 0 {
                            last_sample_in_ctts_run = 0;
                        }
                        last_sample_in_ctts_run +=
                            ctts.entries[ctts_run_index as usize].sample_count as i64;
                    }

                    // dts shift is determined by the smallest negative sample offset:
                    // https://github.com/FFmpeg/FFmpeg/blob/455db6fe109cf905fe518ea2690495948937438f/libavformat/mov.c#L3671
                    let offset = ctts.entries[ctts_run_index as usize].sample_offset as i64;
                    if offset < 0 {
                        dts_shift = dts_shift.max(-offset);
                    }

                    decode_timestamp + offset
                } else {
                    decode_timestamp
                };
                min_composition_timestamp = min_composition_timestamp.min(composition_timestamp);

                let is_sync = if let Some(stss) = &stbl.stss {
                    if last_stss_index < stss.entries.len()
                        && sample_n == stss.entries[last_stss_index] as usize - 1
                    {
                        last_stss_index += 1;
                        true
                    } else {
                        false
                    }
                } else {
                    true
                };

                samples.push(Sample {
                    id: samples.len() as u32,
                    timescale,
                    size,
                    offset,
                    decode_timestamp,
                    composition_timestamp,
                    is_sync,
                    duration: 0, // filled once we know next sample timestamp
                });
                sample_n += 1;
            }

            if let Some(last_sample) = samples.last_mut() {
                last_sample.duration =
                    trak.mdia.mdhd.duration - last_sample.decode_timestamp as u64;
            }

            // Fixup all DTS by the dts shift if there's one.
            // https://github.com/FFmpeg/FFmpeg/blob/455db6fe109cf905fe518ea2690495948937438f/libavformat/mov.c#L4271
            if dts_shift > 0 {
                for sample in &mut samples {
                    sample.decode_timestamp -= dts_shift;
                }
            }

            // Shift both DTS & CTS by the smallest CTS.
            // For details, see declaration of `min_composition_timestamp` above.
            if min_composition_timestamp != 0 {
                for sample in &mut samples {
                    sample.decode_timestamp -= min_composition_timestamp;
                    sample.composition_timestamp -= min_composition_timestamp;
                }
            }

            tracks.insert(
                trak.tkhd.track_id,
                Track {
                    track_id: trak.tkhd.track_id,
                    width: trak.tkhd.width.value(),
                    height: trak.tkhd.height.value(),
                    first_traf_merged: false,
                    timescale: trak.mdia.mdhd.timescale as u64,
                    duration: trak.mdia.mdhd.duration,
                    kind: trak.mdia.minf.stbl.stsd.kind(),
                    samples,
                },
            );
        }

        tracks
    }

    /// In case the input file is fragmented, it will contain one or more `moof` boxes,
    /// which must be processed to obtain the full list of samples for each track.
    fn update_sample_list(&self, tracks: &mut BTreeMap<TrackId, Track>) -> Result<()> {
        let mut last_run_position = 0;

        for moof in &self.moofs {
            // process moof to update sample list
            for traf in &moof.trafs {
                let track_id = traf.tfhd.track_id;
                let track = tracks
                    .get_mut(&track_id)
                    .ok_or(Error::TrakNotFound(track_id))?;
                let trak = self
                    .moov
                    .traks
                    .iter()
                    .find(|trak| trak.tkhd.track_id == track_id)
                    .ok_or(Error::TrakNotFound(track_id))?;
                let trex = if let Some(mvex) = &self.moov.mvex {
                    mvex.trexs
                        .iter()
                        .find(|trex| trex.track_id == track_id)
                        .ok_or(Error::BoxInTrafNotFound(track_id, BoxType::TrexBox))?
                        .clone()
                } else {
                    Default::default()
                };

                let default_sample_duration = traf
                    .tfhd
                    .default_sample_duration
                    .unwrap_or(trex.default_sample_duration);
                let default_sample_size = traf
                    .tfhd
                    .default_sample_size
                    .unwrap_or(trex.default_sample_size);
                let default_sample_flags = traf
                    .tfhd
                    .default_sample_flags
                    .unwrap_or(trex.default_sample_flags);

                for (traf_idx, trun) in traf.truns.iter().enumerate() {
                    for sample_n in 0..trun.sample_count as usize {
                        let mut sample_flags = default_sample_flags;
                        if trun.flags & TrunBox::FLAG_SAMPLE_FLAGS != 0 {
                            sample_flags = trun
                                .sample_flags
                                .get(sample_n)
                                .copied()
                                .unwrap_or(sample_flags);
                        } else if sample_n == 0
                            && (trun.flags & TrunBox::FLAG_FIRST_SAMPLE_FLAGS != 0)
                        {
                            sample_flags = trun.first_sample_flags.unwrap_or(sample_flags);
                        }

                        let decode_timestamp = if track.first_traf_merged || sample_n > 0 {
                            let prev = &track.samples[track.samples.len() - 1];
                            prev.decode_timestamp + prev.duration.cast_signed()
                        } else {
                            track.first_traf_merged = true;
                            traf.tfdt
                                .as_ref()
                                .map_or(0, |tfdt| tfdt.base_media_decode_time.cast_signed())
                        };

                        let composition_timestamp = if trun.flags & TrunBox::FLAG_SAMPLE_CTS != 0 {
                            decode_timestamp
                                + trun.sample_cts.get(sample_n).copied().unwrap_or(0) as i64
                        } else {
                            decode_timestamp
                        };

                        let duration = trun
                            .sample_durations
                            .get(sample_n)
                            .copied()
                            .unwrap_or(default_sample_duration)
                            as u64;

                        let base_data_offset_present =
                            traf.tfhd.flags & TfhdBox::FLAG_BASE_DATA_OFFSET != 0;
                        let default_base_is_moof =
                            traf.tfhd.flags & TfhdBox::FLAG_DEFAULT_BASE_IS_MOOF != 0;
                        let data_offset_present = trun.flags & TrunBox::FLAG_DATA_OFFSET != 0;
                        let base_data_offset = if !base_data_offset_present {
                            if !default_base_is_moof {
                                if sample_n == 0 {
                                    // the first sample in the track fragment
                                    moof.start // the position of the first byte of the enclosing Movie Fragment Box
                                } else {
                                    last_run_position // the offset of the previous sample
                                }
                            } else {
                                moof.start
                            }
                        } else {
                            traf.tfhd.base_data_offset.unwrap_or(moof.start)
                        };

                        let sample_size =
                            trun.sample_sizes
                                .get(sample_n)
                                .copied()
                                .unwrap_or(default_sample_size) as u64;

                        // Sample offset in bytes. (Must be positive, otherwise this would be outside of the file.)
                        let sample_offset = if traf_idx == 0 && sample_n == 0 {
                            if data_offset_present {
                                base_data_offset
                                    .saturating_add_signed(trun.data_offset.unwrap_or(0) as i64)
                            } else {
                                base_data_offset
                            }
                        } else {
                            last_run_position
                        };

                        last_run_position = sample_offset + sample_size;

                        track.samples.push(Sample {
                            id: track.samples.len() as u32,
                            // Bit 16 of the 32-bit sample flags is `sample_is_non_sync_sample`
                            // (ISO/IEC 14496-12 §8.8.3.1), so a sync sample (keyframe) is one
                            // where that bit is *clear*.
                            is_sync: (sample_flags >> 16) & 0x1 == 0,
                            size: sample_size,
                            offset: sample_offset,
                            timescale: trak.mdia.mdhd.timescale as u64,
                            decode_timestamp,
                            composition_timestamp,
                            duration,
                        });
                    }
                }
            }
        }

        Ok(())
    }

    /// Update track metadata after all samples have been read
    fn update_tracks(&mut self) {
        for track in self.tracks.values_mut() {
            if track.duration == 0 {
                track.duration = track
                    .samples
                    .last()
                    .map(|v| v.duration.saturating_add_signed(v.composition_timestamp))
                    .unwrap_or_default();
            }
        }
    }
}

pub struct Track {
    /// Internal field used when decoding a fragmented MP4 file.
    first_traf_merged: bool,

    pub width: u16,
    pub height: u16,

    pub track_id: u32,

    /// Timescale of the sample.
    ///
    /// One time unit is equal to `1.0 / timescale` seconds.
    pub timescale: u64,

    /// Duration of the track in time units.
    pub duration: u64,

    pub kind: Option<TrackKind>,

    /// List of samples in the track.
    pub samples: Vec<Sample>,
}

impl Track {
    pub fn trak<'a>(&self, mp4: &'a Mp4) -> &'a TrakBox {
        let Some(trak) = mp4
            .moov
            .traks
            .iter()
            .find(|trak| trak.tkhd.track_id == self.track_id)
        else {
            // `Track` structs are only constructed when we have `trak` boxes,
            // so unless the user removes the `trak` box from the `Mp4`, it
            // will always be present.
            unreachable!("track with id \"{}\" not found", self.track_id);
        };

        trak
    }

    pub fn raw_codec_config(&self, mp4: &Mp4) -> Option<Vec<u8>> {
        let sample_description = &self.trak(mp4).mdia.minf.stbl.stsd;

        match &sample_description.contents {
            StsdBoxContent::Av01(content) => Some(content.av1c.raw.clone()),
            StsdBoxContent::Avc1(content) => Some(content.avcc.raw.clone()),
            StsdBoxContent::Hev1(content) | StsdBoxContent::Hvc1(content) => {
                Some(content.hvcc.raw.clone())
            }
            StsdBoxContent::Vp08(content) => Some(content.vpcc.raw.clone()),
            StsdBoxContent::Vp09(content) => Some(content.vpcc.raw.clone()),
            StsdBoxContent::Mp4a(_) | StsdBoxContent::Tx3g(_) | StsdBoxContent::Unknown(_) => None,
        }
    }

    pub fn codec_string(&self, mp4: &Mp4) -> Option<String> {
        self.trak(mp4).mdia.minf.stbl.stsd.contents.codec_string()
    }
}

#[derive(Default, Clone, Copy)]
pub struct Sample {
    /// Sample number.
    pub id: u32,

    /// Whether or not an entire frame can be decoded from this one sample,
    /// or if it needs the context of other samples.
    pub is_sync: bool,

    /// Size of the sample in bytes.
    pub size: u64,

    /// Offset of the sample in bytes from the start of the MP4 file.
    pub offset: u64,

    /// Timescale of the sample.
    ///
    /// One time unit is equal to `1.0 / timescale` seconds.
    pub timescale: u64,

    /// Timestamp of the sample at which it should be decoded,
    /// in time units.
    ///
    /// This is offsetted:
    /// * with decode timestamp shift determined from negative sample offsets
    /// * such that the first [`Self::composition_timestamp`] is zero.
    pub decode_timestamp: i64,

    /// Timestamp of the sample at which the sample should be displayed,
    /// in time units.
    ///
    /// This is offsetted such that the first composition timestamp is zero.
    pub composition_timestamp: i64,

    /// Duration of the sample in time units.
    pub duration: u64,
}

impl Sample {
    /// Returns the range of bytes in the input data that this sample covers.
    pub fn byte_range(&self) -> std::ops::Range<usize> {
        self.offset as usize..(self.offset + self.size) as usize
    }
}

#[expect(
    clippy::missing_fields_in_debug,
    reason = "Omit noisy fields from debug output"
)]
impl std::fmt::Debug for Track {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Track")
            .field("first_traf_merged", &self.first_traf_merged)
            .field("kind", &self.kind)
            .field("timescale", &self.timescale)
            .field("duration", &self.duration)
            .finish()
    }
}

#[expect(
    clippy::missing_fields_in_debug,
    reason = "Omit noisy fields from debug output"
)]
impl std::fmt::Debug for Sample {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Sample")
            .field("is_sync", &self.is_sync)
            .field("size", &self.size)
            .field("offset", &self.offset)
            .field("decode_timestamp", &self.decode_timestamp)
            .field("composition_timestamp", &self.composition_timestamp)
            .field("duration", &self.duration)
            .finish()
    }
}
