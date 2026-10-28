// Gathers what the edit renders from: the recorded takes and their event logs, a few files
// from the demo drive, the app icon, and pinned music and sound effects.
//
//   node prepare.mjs [--takes <folder>] [--drive <demo drive>] [--update-lock]
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const home = process.env.USERPROFILE ?? process.env.HOME ?? ".";
const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  if (i < 0) return fallback;
  const value = process.argv[i + 1];
  if (value === undefined || value.startsWith("--")) throw new Error(`${name} needs a folder.`);
  return value;
};
const takes = resolve(arg("--takes", join(home, "FindItDemo", "takes")));
const drive = resolve(arg("--drive", join(home, "FindItDemo", "Portable SSD")));
const cache = resolve(arg("--cache", join(home, "FindItDemo", "cache", "edit")));
const updateLock = process.argv.includes("--update-lock");
const publicDir = join(here, "public");
const assetsPath = join(here, "assets.json");
const assets = JSON.parse(readFileSync(assetsPath, "utf8"));

const copy = (from, to) => { mkdirSync(dirname(to), { recursive: true }); copyFileSync(from, to); };
const sha256 = (path) => createHash("sha256").update(readFileSync(path)).digest("hex");

// Takes: video into public/, logs into one generated module the edit imports.
if (!existsSync(takes)) throw new Error(`No takes at ${takes}. Record them with demo-video/recorder first.`);
const logs = {};
for (const name of readdirSync(takes).filter((f) => f.endsWith(".json")).sort()) {
  const take = name.slice(0, -5);
  if (!existsSync(join(takes, take + ".mp4"))) continue;
  logs[take] = JSON.parse(readFileSync(join(takes, name), "utf8"));
  copy(join(takes, take + ".mp4"), join(publicDir, "takes", take + ".mp4"));
}
mkdirSync(join(here, "src", "generated"), { recursive: true });
writeFileSync(join(here, "src", "generated", "takes.json"), JSON.stringify(logs, null, 1));
console.log(`Takes: ${Object.keys(logs).join(", ")}`);

for (const item of assets.fromDrive) copy(join(drive, item.src), join(publicDir, item.file));
copy(join(here, "..", "..", "src", "FindIt.App", "Assets", "FindIt.png"), join(publicDir, "findit.png"));

for (const item of assets.downloads) {
  const cached = join(cache, item.file);
  if (!existsSync(cached)) {
    const response = await fetch(item.url, { headers: { "User-Agent": "Mozilla/5.0 (FindIt demo edit)" } });
    if (!response.ok) throw new Error(`${item.url}: HTTP ${response.status}`);
    mkdirSync(dirname(cached), { recursive: true });
    writeFileSync(cached, Buffer.from(await response.arrayBuffer()));
  }
  const digest = sha256(cached);
  if (!item.sha256) {
    if (!updateLock) throw new Error(`${item.file} is not pinned. Run with --update-lock to record its SHA-256.`);
    item.sha256 = digest;
  } else if (item.sha256 !== digest) throw new Error(`SHA-256 mismatch for ${item.file}: expected ${item.sha256}, got ${digest}`);
  copy(cached, join(publicDir, item.file));
}
if (updateLock) writeFileSync(assetsPath, JSON.stringify(assets, null, 2) + "\n");
console.log("Assets ready in public/.");
