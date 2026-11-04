import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { brotliDecompressSync, gunzipSync } from "node:zlib";

const [buildPath, expectedMegabytes] = process.argv.slice(2);
const fail = (message) => {
  console.log(`::error title=Web build check::${message}`);
  process.exit(1);
};
if (!buildPath || !expectedMegabytes) fail("Usage: check_web_build.mjs <build folder> <maximum memory MB>");

const buildFolder = join(buildPath, "Build");
const wasmName = readdirSync(buildFolder).find((name) => /\.wasm(\.br|\.gz|\.unityweb)?$/.test(name));
if (!wasmName) fail(`No .wasm file in ${buildFolder}`);
const isWasm = (data) => data.length > 8 && data.readUInt32LE(0) === 0x6d736100;
let wasm = readFileSync(join(buildFolder, wasmName));
try {
  if (wasm[0] === 0x1f && wasm[1] === 0x8b) wasm = gunzipSync(wasm);
  else if (!isWasm(wasm)) wasm = brotliDecompressSync(wasm);
} catch (error) {
  fail(`${wasmName} could not be decompressed: ${error.message}`);
}
if (!isWasm(wasm)) fail(`${wasmName} is not a WebAssembly module`);

let offset = 8;
const byte = () => wasm[offset++];
const leb = () => {
  let value = 0, scale = 1, current;
  do {
    current = byte();
    value += (current & 0x7f) * scale;
    scale *= 128;
  } while (current & 0x80);
  return value;
};
const skip = (length) => { offset += length; };
const limits = () => {
  const flags = byte(), minimum = leb(), maximum = flags & 1 ? leb() : undefined;
  return { flags, minimum, maximum };
};
const memories = [];
while (offset < wasm.length) {
  const id = byte(), size = leb(), end = offset + size;
  if (id === 2) {
    for (let count = leb(); count > 0; count--) {
      skip(leb());
      skip(leb());
      const kind = byte();
      if (kind === 0) leb();
      else if (kind === 1) { byte(); limits(); }
      else if (kind === 2) memories.push(limits());
      else if (kind === 3) { byte(); byte(); }
      else if (kind === 4) { byte(); leb(); }
      else fail(`Unknown import kind ${kind} in ${wasmName}`);
    }
  } else if (id === 5) {
    for (let count = leb(); count > 0; count--) memories.push(limits());
  }
  offset = end;
}

if (memories.length !== 1) fail(`Expected one memory in ${wasmName}, found ${memories.length}`);
const [memory] = memories;
const is64Bit = (memory.flags & 4) !== 0;
const maximumMegabytes = memory.maximum === undefined ? undefined : memory.maximum / 16;
console.log(`${wasmName}: ${is64Bit ? "64" : "32"}-bit memory, maximum ${maximumMegabytes ?? "unset"} MB`);
if (!is64Bit) fail(`${wasmName} uses 32-bit memory; the web player must be 64-bit (Maximum Memory Size above 4096 MB)`);
if (maximumMegabytes !== Number(expectedMegabytes)) fail(`${wasmName} allows ${maximumMegabytes ?? "no maximum"} MB of memory, expected ${expectedMegabytes} MB`);
