import { AbsoluteFill, Audio, OffthreadVideo, Sequence, staticFile, useCurrentFrame } from "remotion";
import { FPS, type Shot } from "../engine/shot";
import { Cursor } from "./Cursor";

/**
 * A recorded take as a floating window: the video, cut and sped up as the shot says, framed by
 * its camera, with a drawn pointer and the interface sounds of what happens in it.
 */
export const AppShot: React.FC<{ shot: Shot; sounds?: boolean }> = ({ shot, sounds = true }) => {
  const frame = useCurrentFrame();
  const cam = shot.camera(frame);
  const { width, height } = shot.take.frame;
  const left = cam.tx - cam.fx * cam.scale;
  const top = cam.ty - cam.fy * cam.scale;
  const p = shot.pointer(frame);
  const src = staticFile(`takes/${shot.spec.take}.mp4`);
  const c = cam.clip;
  const radius = 16 / cam.scale;
  return (
    <AbsoluteFill>
      {/* The shadow follows the clipped shape: whole window, or the part lifted out as a card. */}
      <AbsoluteFill style={{ filter: "drop-shadow(0 34px 60px rgba(0,0,0,0.5)) drop-shadow(0 10px 18px rgba(0,0,0,0.3))" }}>
        <div
          style={{
            position: "absolute", left, top, width, height,
            transform: `scale(${cam.scale})`, transformOrigin: "0 0", background: "#f3f3f3",
            clipPath: `inset(${c.y}px ${width - c.x - c.w}px ${height - c.y - c.h}px ${c.x}px round ${radius}px)`,
          }}
        >
          {shot.segments.map((s, i) => (
            <Sequence key={i} from={Math.round(s.start)} durationInFrames={Math.max(1, Math.round(s.end) - Math.round(s.start))} layout="none">
              <OffthreadVideo src={src} startFrom={Math.round((s.from / 1000) * FPS)} playbackRate={s.speed} muted style={{ width, height, display: "block" }} />
            </Sequence>
          ))}
        </div>
      </AbsoluteFill>
      {/* The pointer belongs to the window; it fades away while part of the window is lifted out as a card. */}
      {shot.spec.cursor !== false && (
        <Cursor x={left + p.x * cam.scale} y={top + p.y * cam.scale} press={p.press} ripple={p.ripple}
          opacity={Math.min(1, Math.max(0, ((c.w * c.h) / (width * height) - 0.45) / 0.4))} />
      )}
      {sounds && (
        <>
          {shot.framesOf("type").map((f, i) => (
            <Sequence key={`k${i}`} from={Math.round(f)} durationInFrames={20} layout="none">
              <Audio src={staticFile("sfx/key.mp3")} volume={0.09 + ((i * 37) % 10) / 250} playbackRate={0.94 + ((i * 53) % 13) / 100} />
            </Sequence>
          ))}
          {[...shot.framesOf("click"), ...shot.framesOf("focus")].map((f, i) => (
            <Sequence key={`c${i}`} from={Math.round(f)} durationInFrames={30} layout="none">
              <Audio src={staticFile("sfx/click.mp3")} volume={0.32} />
            </Sequence>
          ))}
          {shot.framesOf("mark", "results").map((f, i) => (
            <Sequence key={`r${i}`} from={Math.round(f)} durationInFrames={50} layout="none">
              <Audio src={staticFile("sfx/pop.mp3")} volume={0.22} />
            </Sequence>
          ))}
        </>
      )}
    </AbsoluteFill>
  );
};
