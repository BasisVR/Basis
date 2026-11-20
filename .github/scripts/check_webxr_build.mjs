import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { brotliDecompressSync, gunzipSync } from "node:zlib";

const [buildPath] = process.argv.slice(2);
const fail = (message) => {
  console.log(`::error title=WebXR build check::${message}`);
  process.exit(1);
};
if (!buildPath) fail("Usage: check_webxr_build.mjs <build folder>");

const buildFolder = join(buildPath, "Build");
const read = (pattern) => {
  const name = readdirSync(buildFolder).find((entry) => pattern.test(entry));
  if (!name) fail(`No file matching ${pattern} in ${buildFolder}`);
  let data = readFileSync(join(buildFolder, name));
  if (data[0] === 0x1f && data[1] === 0x8b) {
    try {
      data = gunzipSync(data);
    } catch (error) {
      fail(`${name} could not be decompressed: ${error.message}`);
    }
  } else if (/\.(br|unityweb)$/.test(name)) {
    try {
      data = brotliDecompressSync(data);
    } catch (error) {
      if (/\.br$/.test(name)) fail(`${name} could not be decompressed: ${error.message}`);
    }
  }
  return { name, data };
};
const framework = read(/\.framework\.js(\.br|\.gz|\.unityweb)?$/);
const frameworkText = framework.data.toString("utf8");
for (const marker of ["BasisWebXR_SetData", "XRGPUBinding", "createProjectionLayer"]) {
  if (!frameworkText.includes(marker)) fail(`${framework.name} has no ${marker}, so the WebXR glue was not linked`);
}
const wasm = read(/\.wasm(\.br|\.gz|\.unityweb)?$/);
if (!wasm.data.includes(Buffer.from("Basis WebXR Display"))) fail(`${wasm.name} has no Basis WebXR display provider`);
console.log(`WebXR build check passed: ${framework.name}, ${wasm.name}`);
