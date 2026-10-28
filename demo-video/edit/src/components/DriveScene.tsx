import { AbsoluteFill, Audio, Freeze, Img, interpolate, OffthreadVideo, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { FPS } from "../engine/shot";
import { at, getTake } from "../engine/takes";
import { color, font } from "../theme";
import { Caption } from "./Caption";
import { Cursor } from "./Cursor";

const SCREEN = { w: 720, h: 450 };
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** A frozen frame of a take at a logged moment (see `at`), used as a laptop's screen. */
const Still: React.FC<{ take: string; moment: string }> = ({ take, moment }) => (
  <Freeze frame={Math.round((at(getTake(take), moment) / 1000) * FPS)}>
    <OffthreadVideo src={staticFile(`takes/${take}.mp4`)} muted style={{ width: "100%", height: "100%", objectFit: "cover", objectPosition: "top left" }} />
  </Freeze>
);

const Laptop: React.FC<{ x: number; y: number; children?: React.ReactNode; glow: number }> = ({ x, y, children, glow }) => (
  <div style={{ position: "absolute", left: x - SCREEN.w / 2 - 18, top: y - SCREEN.h / 2 - 18 }}>
    <div style={{ width: SCREEN.w + 36, height: SCREEN.h + 36, borderRadius: 22, background: "linear-gradient(160deg, #2C3445, #161B26)", boxShadow: `0 30px 80px rgba(0,0,0,0.6), 0 0 ${60 * glow}px rgba(76,194,255,${0.25 * glow})`, padding: 18 }}>
      <div style={{ width: SCREEN.w, height: SCREEN.h, borderRadius: 8, overflow: "hidden", background: "#05070D", position: "relative" }}>{children}</div>
    </div>
    <div style={{ width: SCREEN.w + 120, height: 22, marginLeft: -42, marginTop: -2, borderRadius: "4px 4px 18px 18px", background: "linear-gradient(180deg, #3A4356, #1C2230)", boxShadow: "0 18px 40px rgba(0,0,0,0.5)" }} />
  </div>
);

const Drive: React.FC<{ x: number; y: number; rotate: number; led: number }> = ({ x, y, rotate, led }) => (
  <div style={{ position: "absolute", left: x - 70, top: y - 46, width: 140, height: 92, borderRadius: 16, transform: `rotate(${rotate}deg)`,
    background: "linear-gradient(150deg, #3B4458 0%, #232A39 55%, #1A202C 100%)", boxShadow: "0 18px 40px rgba(0,0,0,0.55), inset 0 1px 0 rgba(255,255,255,0.12)" }}>
    <Img src={staticFile("findit.png")} style={{ position: "absolute", left: 16, top: 16, width: 34, height: 34, opacity: 0.95 }} />
    <div style={{ position: "absolute", left: 16, bottom: 14, fontFamily: font, fontSize: 13, color: "rgba(255,255,255,0.75)", letterSpacing: "0.04em" }}>Portable SSD</div>
    <div style={{ position: "absolute", right: 14, top: 16, width: 8, height: 8, borderRadius: 4, background: color.accent, opacity: 0.25 + led * 0.75, boxShadow: `0 0 ${12 * led}px ${color.accent}` }} />
  </div>
);

/** The drive leaves one PC, plugs into another and FindIt opens there with everything intact. */
export const DriveScene: React.FC<{ durationInFrames: number }> = ({ durationInFrames }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const left = { x: 500, y: 640 }, right = { x: 1420, y: 640 };
  const portA = { x: left.x + SCREEN.w / 2 + 110, y: left.y + 160 };
  const portB = { x: right.x - SCREEN.w / 2 - 110, y: right.y + 160 };

  const enter = spring({ frame, fps, config: { damping: 200 } });
  const unplug = interpolate(frame, [40, 62], [0, 1], clamp);
  const travel = spring({ frame: frame - 70, fps, config: { damping: 22, mass: 0.9, stiffness: 90 } });
  const plugged = frame >= 128;
  const t = Math.min(1, travel);
  const driveX = interpolate(t, [0, 1], [portA.x + unplug * 60, portB.x]);
  const driveY = interpolate(t, [0, 1], [portA.y, portB.y]) - Math.sin(t * Math.PI) * 170 - unplug * (1 - t) * 20;
  const screenA = interpolate(frame, [52, 80], [1, 0], clamp);
  const desktopB = interpolate(frame, [136, 156], [0, 1], clamp);
  const open = spring({ frame: frame - 196, fps, config: { damping: 200, mass: 0.6 } });
  const icon = { x: 120, y: 120 };
  const pointerB = { x: interpolate(frame, [150, 182], [440, icon.x], clamp), y: interpolate(frame, [150, 182], [300, icon.y + 4], clamp) };
  const press = frame >= 184 && frame < 192 ? Math.sin(((frame - 184) / 8) * Math.PI) : 0;
  const ledB = plugged ? 0.5 + 0.5 * Math.sin(frame / 4) * (frame < 200 ? 1 : 0) + (frame >= 200 ? 0.5 : 0) : 1;

  return (
    <AbsoluteFill style={{ opacity: interpolate(frame, [durationInFrames - 16, durationInFrames], [1, 0], clamp) }}>
      <div style={{ position: "absolute", inset: 0, transform: `translateY(${(1 - enter) * 40}px)`, opacity: enter }}>
        <Laptop x={left.x} y={left.y} glow={screenA}>
          <div style={{ position: "absolute", inset: 0, opacity: screenA }}>
            <Still take="01-dolphins" moment="click:open+375" />
          </div>
          <div style={{ position: "absolute", inset: 0, background: "radial-gradient(400px 260px at 30% 20%, #16335E, #070D1C 75%)", opacity: 1 - screenA }} />
        </Laptop>
        <Laptop x={right.x} y={right.y} glow={desktopB}>
          <div style={{ position: "absolute", inset: 0, background: "radial-gradient(400px 260px at 30% 20%, #1E4D8C, #0B1630 75%)", opacity: desktopB }}>
            <div style={{ position: "absolute", left: icon.x - 32, top: icon.y - 34, width: 64, textAlign: "center", fontFamily: font, fontSize: 12, color: "#fff" }}>
              <Img src={staticFile("findit.png")} style={{ width: 44, height: 44, filter: "drop-shadow(0 4px 8px rgba(0,0,0,0.4))" }} />
              <div style={{ marginTop: 4, textShadow: "0 1px 3px rgba(0,0,0,0.8)" }}>FindIt.exe</div>
            </div>
            {frame < 200 && <Cursor x={pointerB.x} y={pointerB.y} press={press} ripple={frame >= 186 && frame < 206 ? (frame - 186) / 20 : -1} opacity={interpolate(frame, [146, 156], [0, 1], clamp)} />}
            <div style={{ position: "absolute", inset: 0, transformOrigin: `${icon.x}px ${icon.y}px`, transform: `scale(${0.08 + 0.92 * open})`, opacity: open, borderRadius: 6, overflow: "hidden" }}>
              <Still take="02-balloons" moment="focus-330" />
            </div>
          </div>
        </Laptop>
        {/* The cable from whichever laptop holds the drive. */}
        <svg style={{ position: "absolute", inset: 0, width: "100%", height: "100%", overflow: "visible" }}>
          {unplug < 1 && <path d={`M ${left.x + SCREEN.w / 2 + 36} ${portA.y} C ${portA.x - 40} ${portA.y}, ${portA.x - 50} ${portA.y}, ${driveX - 70} ${driveY}`} stroke="#1A1F2B" strokeWidth={9} fill="none" opacity={1 - unplug} />}
          {plugged && <path d={`M ${right.x - SCREEN.w / 2 - 36} ${portB.y} C ${portB.x + 40} ${portB.y}, ${portB.x + 50} ${portB.y}, ${driveX + 70} ${driveY}`} stroke="#1A1F2B" strokeWidth={9} fill="none" />}
        </svg>
        <Drive x={driveX} y={driveY} rotate={Math.sin(t * Math.PI) * -8} led={ledB} />
      </div>
      <Caption title="It all lives on the drive." sub="Plug it into any Windows PC. No install, no internet, no cloud." from={18} to={durationInFrames - 22} x={960} y={190} width={1300} align="center" />
      <Sequence from={66} durationInFrames={60} layout="none"><Audio src={staticFile("sfx/whoosh.mp3")} volume={0.35} /></Sequence>
      <Sequence from={128} durationInFrames={30} layout="none"><Audio src={staticFile("sfx/click.mp3")} volume={0.4} /></Sequence>
      <Sequence from={186} durationInFrames={30} layout="none"><Audio src={staticFile("sfx/click.mp3")} volume={0.3} /></Sequence>
    </AbsoluteFill>
  );
};
