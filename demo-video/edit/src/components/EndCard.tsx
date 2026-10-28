import { AbsoluteFill, Img, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { color, font } from "../theme";

export const EndCard: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const rise = (delay: number) => spring({ frame: frame - delay, fps, config: { damping: 200, mass: 0.8 } });
  const step = (delay: number) => ({ opacity: rise(delay), transform: `translateY(${(1 - rise(delay)) * 24}px)` });
  return (
    <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", fontFamily: font, color: color.ink }}>
      <div style={{ display: "flex", alignItems: "center", gap: 28, ...step(0) }}>
        <Img src={staticFile("findit.png")} style={{ width: 120, height: 120, filter: "drop-shadow(0 12px 30px rgba(10,108,214,0.45))" }} />
        <div style={{ fontSize: 112, fontWeight: 600, letterSpacing: "-0.02em" }}>FindIt</div>
      </div>
      <div style={{ fontSize: 40, color: color.muted, marginTop: 26, ...step(8) }}>Offline search that travels with your files.</div>
      <div style={{ marginTop: 52, padding: "16px 34px", borderRadius: 999, background: "rgba(76,194,255,0.12)", border: "1.5px solid rgba(76,194,255,0.45)", color: color.accent, fontSize: 34, fontWeight: 600, ...step(16) }}>
        github.com/adilzubair/FindIt
      </div>
      <div style={{ marginTop: 26, fontSize: 26, color: "rgba(226,234,246,0.55)", ...step(22) }}>Free and open source · Windows 10 and 11</div>
    </AbsoluteFill>
  );
};
