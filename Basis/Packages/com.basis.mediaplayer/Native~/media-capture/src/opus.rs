use audiopus_sys::{
    OPUS_APPLICATION_AUDIO, OPUS_SET_BITRATE_REQUEST, OPUS_SET_COMPLEXITY_REQUEST,
    OPUS_SET_SIGNAL_REQUEST, OPUS_SIGNAL_MUSIC, opus_encode_float, opus_encoder_create,
    opus_encoder_ctl, opus_encoder_destroy,
};

use crate::{AUDIO_CHANNELS, AUDIO_FRAME, AUDIO_RATE, CaptureError};

pub struct OpusEncoder {
    raw: *mut audiopus_sys::OpusEncoder,
    out: Vec<u8>,
}

// SAFETY: the encoder state is owned by this value and only touched through
// &mut self, so moving it to another thread is sound.
unsafe impl Send for OpusEncoder {}

impl OpusEncoder {
    pub fn new(bitrate_kbps: u32) -> Result<Self, CaptureError> {
        let mut error = 0;
        // SAFETY: plain constructor call; the returned pointer is checked
        // before use and owned by this value from here on.
        let raw = unsafe {
            opus_encoder_create(
                AUDIO_RATE as i32,
                AUDIO_CHANNELS as i32,
                OPUS_APPLICATION_AUDIO,
                &mut error,
            )
        };
        if raw.is_null() || error != 0 {
            return Err(CaptureError(format!(
                "opus encoder create failed ({error})"
            )));
        }
        // SAFETY: raw is a live encoder; the variadic ctl takes one i32.
        unsafe {
            opus_encoder_ctl(raw, OPUS_SET_BITRATE_REQUEST, (bitrate_kbps * 1000) as i32);
            opus_encoder_ctl(raw, OPUS_SET_COMPLEXITY_REQUEST, 8i32);
            opus_encoder_ctl(raw, OPUS_SET_SIGNAL_REQUEST, OPUS_SIGNAL_MUSIC);
        }
        Ok(Self {
            raw,
            out: vec![0; 4000],
        })
    }

    pub fn encode(&mut self, pcm: &[f32]) -> Result<&[u8], CaptureError> {
        if pcm.len() != AUDIO_FRAME * AUDIO_CHANNELS as usize {
            return Err(CaptureError("opus frame must be 20 ms".into()));
        }
        // SAFETY: pcm holds one full interleaved frame and out is a writable
        // buffer of the stated length.
        let n = unsafe {
            opus_encode_float(
                self.raw,
                pcm.as_ptr(),
                AUDIO_FRAME as i32,
                self.out.as_mut_ptr(),
                self.out.len() as i32,
            )
        };
        if n < 0 {
            return Err(CaptureError(format!("opus encode failed ({n})")));
        }
        Ok(&self.out[..n as usize])
    }
}

impl Drop for OpusEncoder {
    fn drop(&mut self) {
        // SAFETY: raw came from opus_encoder_create and is destroyed once.
        unsafe { opus_encoder_destroy(self.raw) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_silence_and_tone() {
        let mut encoder = OpusEncoder::new(128).unwrap();
        let silence = vec![0f32; AUDIO_FRAME * 2];
        assert!(!encoder.encode(&silence).unwrap().is_empty());
        let tone: Vec<f32> = (0..AUDIO_FRAME * 2)
            .map(|i| ((i / 2) as f32 * 0.05).sin() * 0.3)
            .collect();
        let packet = encoder.encode(&tone).unwrap().len();
        assert!(packet > 10);
        assert!(encoder.encode(&tone[..10]).is_err());
    }
}
