import { AbsoluteFill, useCurrentFrame } from "remotion";
import { color } from "../theme";

/** Deep blue backdrop with two slow light pools and fine grain, so the light app window pops. */
export const Background: React.FC = () => {
  const frame = useCurrentFrame();
  const drift = Math.sin(frame / 240) * 40;
  return (
    <AbsoluteFill style={{ background: color.night }}>
      <AbsoluteFill
        style={{
          background: `radial-gradient(1200px 800px at ${22 + drift / 40}% ${18 + drift / 60}%, ${color.glow} 0%, ${color.navy} 45%, transparent 80%),
                       radial-gradient(900px 700px at ${88 - drift / 50}% ${92}%, #12306A 0%, transparent 70%)`,
          opacity: 0.95,
        }}
      />
      <AbsoluteFill style={{ opacity: 0.07, mixBlendMode: "overlay" }}>
        <svg width="100%" height="100%">
          <filter id="grain">
            <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" seed={frame % 8} stitchTiles="stitch" />
          </filter>
          <rect width="100%" height="100%" filter="url(#grain)" />
        </svg>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
