import raw from "../generated/takes.json";

export type Rect = { x: number; y: number; w: number; h: number };
export type TakeEvent = { t: number; type: string; target: string | null; rect: number[] | null; text: string | null };
export type Take = { take: string; dpi: number; frame: { width: number; height: number }; events: TakeEvent[] };

const takes = raw as unknown as Record<string, Take>;

export const getTake = (name: string): Take => {
  const take = takes[name];
  if (!take) throw new Error(`Take ${name} is missing. Record it, then run "node prepare.mjs".`);
  return take;
};

const toRect = (r: number[]): Rect => ({ x: r[0], y: r[1], w: r[2], h: r[3] });

/**
 * A moment in a take, in milliseconds of its video:
 *   "start" | "end" | "focus" | "type:first" | "type:last" | "mark:<label>" | "click:<target prefix>" | "playing"
 * with an optional "+ms" or "-ms" offset, e.g. "click:open+400".
 */
export const at = (take: Take, ref: string): number => {
  const m = /^(.*?)([+-]\d+)?$/.exec(ref)!;
  const base = m[1];
  const offset = m[2] ? Number(m[2]) : 0;
  const events = take.events;
  const find = (pred: (e: TakeEvent) => boolean, last = false) => {
    const list = events.filter(pred);
    const e = last ? list[list.length - 1] : list[0];
    if (!e) throw new Error(`No event "${ref}" in take ${take.take}`);
    return e.t;
  };
  let t: number;
  if (base === "start") t = 0;
  else if (base === "end") t = find((e) => e.type === "mark" && e.target === "end");
  else if (base === "type:first") t = find((e) => e.type === "type");
  else if (base === "type:last") t = find((e) => e.type === "type", true);
  else if (base.startsWith("mark:")) t = find((e) => e.type === "mark" && e.target === base.slice(5));
  else if (base.startsWith("click:")) t = find((e) => e.type === "click" && (e.target ?? "").startsWith(base.slice(6)));
  else t = find((e) => e.type === base);
  return t + offset;
};

/**
 * A rectangle in the take's pixels: "full", a logged element ("MomentsList", "PreviewText"),
 * a result tile by position ("tile0") or by name ("tile:GX010274"), the clicked target
 * ("click:open"), or the search box ("focus"). Join several with "|" for their union.
 */
export const rectOf = (take: Take, ref: string): Rect => {
  // "a|b" is the union of a and b; "a|b&c" intersects that union with c (e.g. a list with the part that is on screen).
  if (ref.includes("&")) {
    return ref.split("&").map((r) => rectOf(take, r)).reduce((a, b) => {
      const x = Math.max(a.x, b.x), y = Math.max(a.y, b.y);
      return { x, y, w: Math.max(0, Math.min(a.x + a.w, b.x + b.w) - x), h: Math.max(0, Math.min(a.y + a.h, b.y + b.h) - y) };
    });
  }
  const parts = ref.split("|").map((part): Rect => {
    if (part.startsWith("rect:")) { const [x, y, w, h] = part.slice(5).split(",").map(Number); return { x, y, w, h }; }
    if (part === "full") return { x: 0, y: 0, w: take.frame.width, h: take.frame.height };
    const named = (pred: (e: TakeEvent) => boolean) => {
      const e = [...take.events].reverse().find((x) => x.rect && pred(x));
      if (!e) throw new Error(`No rectangle "${part}" in take ${take.take}`);
      return toRect(e.rect!);
    };
    if (part === "focus") return named((e) => e.type === "focus");
    // The first C tiles of the first R rows of results, whatever the gallery's column count.
    const grid = /^grid:(\d+)x(\d+)$/.exec(part);
    if (grid) {
      const mark = [...take.events].reverse().find((e) => e.type === "mark" && e.target === "results");
      const tiles = take.events.filter((e) => e.type === "rect" && e.t === mark?.t && (e.target ?? "").startsWith("tile")).map((e) => toRect(e.rect!));
      const rows = [...new Set(tiles.map((r) => r.y))].sort((a, b) => a - b).slice(0, Number(grid[2]));
      const chosen = rows.flatMap((y) => tiles.filter((r) => r.y === y).sort((a, b) => a.x - b.x).slice(0, Number(grid[1])));
      if (chosen.length === 0) throw new Error(`No result tiles for "${part}" in take ${take.take}`);
      const x = Math.min(...chosen.map((r) => r.x)), y = Math.min(...chosen.map((r) => r.y));
      return { x, y, w: Math.max(...chosen.map((r) => r.x + r.w)) - x, h: Math.max(...chosen.map((r) => r.y + r.h)) - y };
    }
    if (part.startsWith("click:")) return named((e) => e.type === "click" && (e.target ?? "").startsWith(part.slice(6)));
    if (part.startsWith("tile:")) return named((e) => e.type === "rect" && (e.target ?? "").startsWith("tile") && (e.target ?? "").includes(part.slice(5)));
    if (/^tile\d+$/.test(part)) return named((e) => e.type === "rect" && (e.target ?? "").startsWith(part + ":"));
    return named((e) => e.type === "rect" && e.target === part);
  });
  // Clip to the window: scrolled lists report their full, partly hidden extent.
  const x = Math.max(0, Math.min(...parts.map((r) => r.x)));
  const y = Math.max(0, Math.min(...parts.map((r) => r.y)));
  const right = Math.min(take.frame.width, Math.max(...parts.map((r) => r.x + r.w)));
  const bottom = Math.min(take.frame.height, Math.max(...parts.map((r) => r.y + r.h)));
  return { x, y, w: right - x, h: bottom - y };
};

export const pad = (r: Rect, top: number, right = top, bottom = top, left = right): Rect =>
  ({ x: r.x - left, y: r.y - top, w: r.w + left + right, h: r.h + top + bottom });
