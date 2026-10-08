# Vendored re_mp4 0.5.1

A copy of the `re_mp4` crate (MIT, see LICENSE), applied through
`[patch.crates-io]` in the workspace root. The lockfile, the original manifest
and the packaged `tests/` (only the licence files of sample media the crate
does not ship) are dropped.
The source matches the crates.io release apart from this change:

- `src/types.rs`, `DataType::try_from`: a metadata `data` box whose type is not
  one of the four the crate names (binary, UTF-8 text, JPEG, signed integer)
  is read as binary. The release returns an error, which fails the `ilst`,
  `meta` and `udta` reads and with them the whole `moov`, so a file whose cover
  is a PNG (type 14) or a BMP (type 27), or that carries any other item of a
  well-known type the crate does not list, could not be opened at all.
  `media-demux/tests/mp4_stream.rs`, `a_png_cover_opens_and_is_the_art`, covers
  it with `fixtures/h264-aac-png-cover.mp4`.
