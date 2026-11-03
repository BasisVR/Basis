use crate::{
    AUDIO_CHANNELS, AUDIO_FRAME, AUDIO_RATE, CaptureError, EncodedAudio, OpusEncoder, Shared,
};

pub(crate) struct Packer {
    encoder: OpusEncoder,
    pending: Vec<f32>,
    written: i64,
    base_us: i64,
}

impl Packer {
    pub(crate) fn new(shared: &Shared) -> Result<Self, CaptureError> {
        Ok(Self {
            encoder: OpusEncoder::new(shared.config.audio_bitrate_kbps)?,
            pending: Vec::with_capacity(AUDIO_FRAME * 4),
            written: 0,
            base_us: shared.now_us(),
        })
    }

    pub(crate) fn push(&mut self, shared: &Shared, samples: &[f32]) -> Result<(), CaptureError> {
        self.pending.extend_from_slice(samples);
        let frame = AUDIO_FRAME * AUDIO_CHANNELS as usize;
        while self.pending.len() >= frame {
            let packet = self.encoder.encode(&self.pending[..frame])?.to_vec();
            let pts_us = self.base_us + self.written * 1_000_000 / i64::from(AUDIO_RATE);
            self.written += AUDIO_FRAME as i64;
            self.pending.drain(..frame);
            shared.push_audio(EncodedAudio {
                data: packet,
                pts_us,
            });
        }
        Ok(())
    }

    pub(crate) fn fill_silence(&mut self, shared: &Shared) -> Result<(), CaptureError> {
        let expected = (shared.now_us() - self.base_us) * i64::from(AUDIO_RATE) / 1_000_000;
        let have = self.written + (self.pending.len() / AUDIO_CHANNELS as usize) as i64;
        let behind = expected - have - (AUDIO_RATE as i64 / 20);
        if behind > 0 {
            let zeros = vec![0f32; behind as usize * AUDIO_CHANNELS as usize];
            self.push(shared, &zeros)?;
        }
        Ok(())
    }
}
