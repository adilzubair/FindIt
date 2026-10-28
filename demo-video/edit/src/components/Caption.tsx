import { interpolate, spring, useCurrentFrame, useVideoConfig } from "remotion";
import { color, font } from "../theme";

export type CaptionSpec = { eyebrow?: string; title: string; sub?: string; from: number; to: number; x?: number; y?: number; width?: number; align?: "left" | "center" };

/** A headline that rises in word by word and fades out. Frames are relative to the enclosing sequence. */
export const Caption: React.FC<CaptionSpec> = ({ eyebrow, title, sub, from, to, x = 110, y = 540, width = 600, align = "left" }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  if (frame < from - 1 || frame > to + 20) return null;
  const out = interpolate(frame, [to, to + 18], [1, 0], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
  const rise = (delay: number) => spring({ frame: frame - from - delay, fps, config: { damping: 200, mass: 0.7 } });
  const words = title.split(" ");
  return (
    <div style={{ position: "absolute", left: align === "center" ? x - width / 2 : x, top: y, width, transform: "translateY(-50%)", fontFamily: font, textAlign: align, opacity: out }}>
      {eyebrow && (
        <div style={{ color: color.accent, fontSize: 24, fontWeight: 600, letterSpacing: "0.14em", textTransform: "uppercase", marginBottom: 18, opacity: rise(0), transform: `translateY(${(1 - rise(0)) * 16}px)` }}>
          {eyebrow}
        </div>
      )}
      <div style={{ color: color.ink, fontSize: 62, fontWeight: 600, lineHeight: 1.08, letterSpacing: "-0.015em" }}>
        {words.map((w, i) => {
          const p = rise(3 + i * 2.2);
          return (
            <span key={i} style={{ display: "inline-block", opacity: p, transform: `translateY(${(1 - p) * 26}px)`, marginRight: "0.26em" }}>
              {w}
            </span>
          );
        })}
      </div>
      {sub && (
        <div style={{ color: color.muted, fontSize: 30, lineHeight: 1.35, marginTop: 22, opacity: rise(8 + words.length * 2), transform: `translateY(${(1 - rise(8 + words.length * 2)) * 14}px)` }}>
          {sub}
        </div>
      )}
    </div>
  );
};
