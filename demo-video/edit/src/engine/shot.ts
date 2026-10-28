import { Easing } from "remotion";
import { at, getTake, pad, rectOf, type Rect, type Take } from "./takes";

export const FPS = 60;
export const WIDTH = 1920;
export const HEIGHT = 1080;

/** Where a shot puts the part of the app it is about, in output pixels. */
export const REGIONS = {
  full: { x: 120, y: 64, w: 1680, h: 952 },
  center: { x: 150, y: 120, w: 1620, h: 840 },
  right: { x: 760, y: 70, w: 1100, h: 940 },
} satisfies Record<string, Rect>;

export type Segment = { from: string; to: string; speed?: number };
export type View = {
  /** Take rectangle reference (see rectOf) or explicit rectangle in take pixels. */
  focus: string | Rect;
  pad?: number | [number, number, number, number];
  into?: keyof typeof REGIONS | Rect;
  /** Multiplies the fitted scale, e.g. 1.1 to crop a little tighter. */
  zoom?: number;
  /** Lift the focused part out of the window as a card, hiding the rest. */
  crop?: boolean;
};
export type CameraKey = View & { at: string; ms?: number };
export type ShotSpec = { take: string; segments: Segment[]; camera: CameraKey[]; cursor?: boolean };

type Clip = { x: number; y: number; w: number; h: number };
type Placed = { scale: number; fx: number; fy: number; tx: number; ty: number; clip: Clip };
type Seg = { from: number; to: number; speed: number; start: number; end: number };

const ease = Easing.bezier(0.6, 0, 0.25, 1);
const clamp01 = (v: number) => Math.min(1, Math.max(0, v));
const lerp = (a: number, b: number, t: number) => a + (b - a) * t;
const blend = (a: Placed, b: Placed, p: number): Placed => ({
  scale: Math.exp(lerp(Math.log(a.scale), Math.log(b.scale), p)),
  fx: lerp(a.fx, b.fx, p), fy: lerp(a.fy, b.fy, p),
  tx: lerp(a.tx, b.tx, p), ty: lerp(a.ty, b.ty, p),
  clip: { x: lerp(a.clip.x, b.clip.x, p), y: lerp(a.clip.y, b.clip.y, p), w: lerp(a.clip.w, b.clip.w, p), h: lerp(a.clip.h, b.clip.h, p) },
});

/** One recorded take, cut, sped up and framed. Frames are output frames from the shot's start. */
export class Shot {
  readonly take: Take;
  readonly segments: Seg[];
  readonly durationInFrames: number;
  private readonly keys: { frame: number; frames: number; start: Placed; view: Placed }[];
  private readonly clicks: { frame: number; x: number; y: number }[];
  private readonly moves: { depart: number; arrive: number; from: { x: number; y: number }; to: { x: number; y: number } }[] = [];
  private readonly restStart: { x: number; y: number };

  constructor(readonly spec: ShotSpec) {
    this.take = getTake(spec.take);
    let cursor = 0;
    this.segments = spec.segments.map((s) => {
      const from = at(this.take, s.from), to = at(this.take, s.to), speed = s.speed ?? 1;
      if (to <= from) throw new Error(`${spec.take}: segment ${s.from} → ${s.to} is empty`);
      const seg = { from, to, speed, start: cursor, end: cursor + ((to - from) / speed / 1000) * FPS };
      cursor = seg.end;
      return seg;
    });
    this.durationInFrames = Math.round(cursor);
    // Each move starts from wherever the camera is when its key arrives, even if the previous move has not finished.
    this.keys = [];
    for (const k of spec.camera) {
      const frame = this.frameAt(at(this.take, k.at)), view = this.place(k), last = this.keys[this.keys.length - 1];
      const start = last ? blend(last.start, last.view, ease(clamp01((frame - last.frame) / last.frames))) : view;
      this.keys.push({ frame, frames: ((k.ms ?? 750) / 1000) * FPS, start, view });
    }
    this.clicks = this.take.events
      .filter((e) => (e.type === "click" || e.type === "focus") && e.rect)
      .map((e) => ({ frame: this.frameAt(e.t), x: e.rect![0] + e.rect![2] / 2, y: e.rect![1] + e.rect![3] / 2 }))
      .filter((c) => c.frame > 0 && c.frame < this.durationInFrames);
    // The pointer enters from below the window, then glides to each click and rests there.
    this.restStart = { x: this.take.frame.width * 0.78, y: this.take.frame.height * 1.08 };
    let from = this.restStart, free = 0;
    for (const c of this.clicks) {
      const arrive = c.frame - 7;
      const travel = Math.min(48, Math.max(20, Math.hypot(c.x - from.x, c.y - from.y) * 0.035));
      const depart = Math.max(free, arrive - travel);
      this.moves.push({ depart, arrive: Math.max(arrive, depart + 1), from, to: { x: c.x, y: c.y } });
      from = { x: c.x, y: c.y };
      free = c.frame + 10;
    }
  }

  /** Output frame (fractional) at which the take reaches `ms`; times cut out map to the next kept moment. */
  frameAt(ms: number): number {
    for (const s of this.segments) {
      if (ms < s.from) return s.start;
      if (ms <= s.to) return s.start + ((ms - s.from) / s.speed / 1000) * FPS;
    }
    return this.segments[this.segments.length - 1].end;
  }

  /** Take time in ms shown at output frame `frame`. */
  msAt(frame: number): number {
    const s = this.segments.find((x) => frame < x.end) ?? this.segments[this.segments.length - 1];
    return s.from + ((frame - s.start) / FPS) * 1000 * s.speed;
  }

  frameOf(ref: string) { return this.frameAt(at(this.take, ref)); }

  private place(view: View): Placed {
    let f = typeof view.focus === "string" ? rectOf(this.take, view.focus) : view.focus;
    if (view.pad !== undefined) f = typeof view.pad === "number" ? pad(f, view.pad) : pad(f, ...view.pad);
    const t = typeof view.into === "object" ? view.into : REGIONS[view.into ?? "center"];
    const scale = Math.min(t.w / f.w, t.h / f.h) * (view.zoom ?? 1);
    const full = { x: 0, y: 0, w: this.take.frame.width, h: this.take.frame.height };
    const clip = view.crop ? { x: Math.max(0, f.x), y: Math.max(0, f.y), w: Math.min(full.w, f.x + f.w) - Math.max(0, f.x), h: Math.min(full.h, f.y + f.h) - Math.max(0, f.y) } : full;
    return { scale, fx: f.x + f.w / 2, fy: f.y + f.h / 2, tx: t.x + t.w / 2, ty: t.y + t.h / 2, clip };
  }

  /** The camera at `frame`: output = (take point − focus) × scale + target. */
  camera(frame: number): Placed {
    let i = this.keys.findLastIndex((k) => k.frame <= frame);
    if (i < 0) i = 0;
    const k = this.keys[i];
    return blend(k.start, k.view, i === 0 ? 1 : ease(clamp01((frame - k.frame) / k.frames)));
  }

  /** Where the pointer is, in take pixels, how pressed it looks (0–1) and the click ripple's progress (0–1, or −1). */
  pointer(frame: number): { x: number; y: number; press: number; ripple: number } {
    let pos = this.restStart;
    for (const m of this.moves) {
      if (frame < m.depart) break;
      if (frame < m.arrive) {
        const p = ease(clamp01((frame - m.depart) / (m.arrive - m.depart)));
        // A slight arc, like a wrist moving a mouse.
        const bow = Math.sin(p * Math.PI) * Math.min(60, Math.hypot(m.to.x - m.from.x, m.to.y - m.from.y) * 0.08);
        pos = { x: lerp(m.from.x, m.to.x, p) + bow, y: lerp(m.from.y, m.to.y, p) - bow * 0.4 };
        break;
      }
      pos = m.to;
    }
    let press = 0, ripple = -1;
    for (const c of this.clicks) {
      const since = frame - c.frame;
      if (since >= -3 && since < 8) press = Math.sin(clamp01((since + 3) / 11) * Math.PI);
      if (since >= 0 && since < 30) ripple = since / 30;
    }
    return { ...pos, press, ripple };
  }

  /** Output frames of events of a type, for sound cues. */
  framesOf(type: string, target?: string): number[] {
    return this.take.events
      .filter((e) => e.type === type && (target === undefined || (e.target ?? "").startsWith(target)))
      .map((e) => this.frameAt(e.t))
      .filter((f) => f >= 0 && f < this.durationInFrames);
  }
}
