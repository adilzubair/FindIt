// Publishes a rendered cut: the MP4 with sound for docs/media, and the silent, looping WebP the README shows.
//
//   npx remotion render Demo out/findit-demo.mp4 --crf=21
//   node export.mjs
import { execFileSync } from "node:child_process";
import { existsSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const ffmpeg = process.env.FFMPEG ?? join(repo, "runtime", "tools", "ffmpeg.exe");
const source = join(here, "out", "findit-demo.mp4");
const media = join(repo, "docs", "media");
if (!existsSync(source)) throw new Error(`Render the cut first: ${source} is missing.`);
if (!existsSync(ffmpeg)) throw new Error(`ffmpeg not found at ${ffmpeg}; set FFMPEG.`);

// The mix renders at about -19 LUFS with peaks near -7 dBFS; 3 dB more brings it to about -16 LUFS, typical for web video.
execFileSync(ffmpeg, [
  "-v", "error", "-y", "-i", source,
  "-c:v", "copy", "-af", "volume=3dB", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
  join(media, "findit-demo.mp4"),
], { stdio: "inherit" });
// The README preview: 960 px wide at 20 fps keeps the UI legible and the file small enough for GitHub to show inline.
execFileSync(ffmpeg, [
  "-v", "error", "-y", "-i", source, "-an",
  "-vf", "fps=20,scale=960:-2:flags=lanczos",
  "-c:v", "libwebp_anim", "-quality", "68", "-compression_level", "6", "-loop", "0",
  join(media, "findit-demo.webp"),
], { stdio: "inherit" });

for (const name of ["findit-demo.mp4", "findit-demo.webp"]) {
  console.log(`${name}: ${(statSync(join(media, name)).size / 1e6).toFixed(1)} MB`);
}
