//! Local file source: the trivial `ByteSource`, used by bm-probe and the
//! fixture lanes.

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

use media_demux::{ByteSource, SourceError};

use crate::{IoError, IoErrorKind};

pub struct FileSource {
    file: File,
    len: u64,
}

impl FileSource {
    pub fn open(path: &Path) -> Result<Self, IoError> {
        let file = File::open(path)
            .map_err(|e| IoError::new(IoErrorKind::File, format!("{}: {e}", path.display())))?;
        let len = file
            .metadata()
            .map_err(|e| IoError::new(IoErrorKind::File, e.to_string()))?
            .len();
        Ok(Self { file, len })
    }
}

impl ByteSource for FileSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(Some(self.len))
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        self.file.seek(SeekFrom::Start(offset))?;
        let mut filled = 0usize;
        while filled < buf.len() {
            let n = self.file.read(&mut buf[filled..])?;
            if n == 0 {
                break;
            }
            filled += n;
        }
        Ok(filled)
    }
}
