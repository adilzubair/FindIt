import { AbsoluteFill, Audio, interpolate, Sequence, staticFile, useCurrentFrame } from "remotion";
import { AppShot } from "./components/AppShot";
import { Background } from "./components/Background";
import { Caption, type CaptionSpec } from "./components/Caption";
import { DriveScene } from "./components/DriveScene";
import { EndCard } from "./components/EndCard";
import { FPS, Shot, type CameraKey } from "./engine/shot";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

// The large search box with the home page's question above it.
const searchBox = (at: string, ms?: number): CameraKey => ({ at, ms, focus: "focus", pad: [230, 70, 330, 70], into: "center" });
const typing = (lead: number, speed: number) => [
  { from: `focus-${lead}`, to: "type:first" },
  { from: "type:first", to: "type:last+120", speed },
  { from: "type:last+120", to: "click:HeroSearchButton+60" },
];

// 1. A described moment inside a long video: the hook.
const dolphins = new Shot({
  take: "01-dolphins",
  segments: [
    { from: "start", to: "focus-500", speed: 1 },
    { from: "focus-500", to: "type:first" },
    { from: "type:first", to: "type:last+120", speed: 2.1 },
    { from: "type:last+120", to: "click:HeroSearchButton+60" },
    { from: "click:HeroSearchButton+60", to: "mark:results+450" },
    { from: "mark:results+450", to: "click:open-600", speed: 1.8 },
    { from: "click:open-600", to: "mark:preview+3900" },
  ],
  camera: [
    { at: "start", focus: "full", into: "full" },
    searchBox("focus-650", 900),
    { at: "click:HeroSearchButton+80", focus: "full", into: "full", ms: 850 },
    // The video, its timeline and the first matching moments; the rest of the list is cut off.
    { at: "click:open+650", focus: "PreviewName|PlaybackTimeline|MomentsList&rect:0,0,2160,1100", pad: 26, into: "right", crop: true, ms: 1000 },
  ],
});

// 2. Photos by what is in them.
const balloons = new Shot({
  take: "02-balloons",
  segments: [...typing(450, 2.2), { from: "click:HeroSearchButton+60", to: "mark:results+3600" }],
  camera: [
    searchBox("focus-450"),
    { at: "click:HeroSearchButton+80", focus: "full", into: "full", ms: 700 },
    { at: "mark:results+250", focus: "grid:4x2", pad: [22, 22, 8, 22], into: "right", crop: true, ms: 1000 },
    { at: "mark:results+1300", focus: "grid:4x2", pad: [22, 22, 8, 22], into: "right", crop: true, zoom: 1.05, ms: 1600 },
  ],
});

// 3. A sentence spoken in a voice memo. The memo plays from 0:00 when its moment is clicked;
// the cut lands just before "and the last ferry back leaves at 7.40" (6.65 s into the memo).
const MEMO_LATENCY_MS = 300;
const MEMO_CUT_MS = 6350;
const ferry = new Shot({
  take: "03-ferry",
  segments: [
    ...typing(450, 2.2),
    { from: "click:HeroSearchButton+60", to: "click:open-550", speed: 1.4 },
    { from: "click:open-550", to: "click:open+1550" },
    { from: `click:moment+${MEMO_CUT_MS + MEMO_LATENCY_MS}`, to: `click:moment+${MEMO_CUT_MS + MEMO_LATENCY_MS + 3400}` },
  ],
  camera: [
    searchBox("focus-450"),
    { at: "click:HeroSearchButton+80", focus: "full", into: "full", ms: 850 },
    { at: "click:open+600", focus: "PreviewName|MomentsList|PreviewText", pad: 36, into: "right", crop: true, ms: 850 },
    { at: `click:moment+${MEMO_CUT_MS + MEMO_LATENCY_MS}`, focus: "MomentsList|PreviewText", pad: [36, 36, 36, 36], into: "right", crop: true, ms: 1100 },
  ],
});

// 4. Words inside a scanned paper document: the card shows the scan, then the passage OCR found, highlighted.
// The rectangle frames "The Landlord will return the Deposit to the Tenant within fourteen (14) days".
const deposit = new Shot({
  take: "04-deposit",
  segments: [
    ...typing(450, 2.2),
    { from: "click:HeroSearchButton+60", to: "click:open-550", speed: 1.4 },
    { from: "click:open-550", to: "scroll+150" },
    { from: "scroll+150", to: "mark:text+3000" },
  ],
  camera: [
    searchBox("focus-450"),
    { at: "click:HeroSearchButton+80", focus: "full", into: "full", ms: 850 },
    { at: "click:open+650", focus: "PreviewName|Close preview|OpenOriginalButton", pad: 26, into: "right", crop: true, ms: 1000 },
    { at: "scroll+150", focus: "rect:1650,730,440,270", pad: 18, into: "right", crop: true, ms: 1100 },
  ],
});

// The preview panel appears about 0.7 s after a click, so each shot frames it from click:open+600 or later,
// and its caption starts once the camera has moved the app clear of the left side (caption text is white).
type Item = { duration: number; render: () => React.ReactNode; captions?: CaptionSpec[]; whoosh?: boolean };
const TRANSITION = 18;
const shotItem = (shot: Shot, caption: Omit<CaptionSpec, "from" | "to">, from: string): Item => ({
  duration: shot.durationInFrames,
  render: () => <AppShot shot={shot} />,
  captions: [{ ...caption, from: shot.frameOf(from), to: shot.durationInFrames - 12 }],
  whoosh: true,
});
const items: Item[] = [
  shotItem(dolphins, { eyebrow: "Videos", title: "Opens at the moment you described." }, "click:open+1400"),
  shotItem(balloons, { eyebrow: "Photos", title: "Found by what's in them, not by filename." }, "mark:results+1000"),
  shotItem(ferry, { eyebrow: "Voice memos", title: "Finds what was said." }, "click:open+1300"),
  shotItem(deposit, { eyebrow: "Documents", title: "Reads documents, even scanned paper." }, "click:open+1400"),
  { duration: 290, render: () => <DriveScene durationInFrames={290} />, whoosh: true },
  { duration: 240, render: () => <EndCard /> },
];
const starts = items.reduce<number[]>((acc, item, i) => [...acc, i === 0 ? 0 : acc[i - 1] + items[i - 1].duration - TRANSITION], []);
export const DURATION = starts[starts.length - 1] + items[items.length - 1].duration;
const ferryStart = starts[2];
const memoSegment = ferry.segments[ferry.segments.length - 1];

const Transition: React.FC<{ duration: number; children: React.ReactNode; first: boolean; last: boolean }> = ({ duration, children, first, last }) => {
  const frame = useCurrentFrame();
  const enter = first ? 1 : interpolate(frame, [0, TRANSITION], [0, 1], clamp);
  const exit = last ? 1 : interpolate(frame, [duration - TRANSITION, duration], [1, 0], clamp);
  const p = Math.min(enter, exit);
  return (
    <AbsoluteFill style={{ opacity: p, transform: `translateY(${(1 - enter) * 34 - (1 - exit) * 20}px) scale(${0.985 + 0.015 * enter})`, filter: p < 1 ? `blur(${(1 - p) * 8}px)` : undefined }}>
      {children}
    </AbsoluteFill>
  );
};

export const Demo: React.FC = () => {
  const frame = useCurrentFrame();
  const memoFrom = ferryStart + Math.round(memoSegment.start);
  const memoTo = ferryStart + Math.round(memoSegment.end);
  const duck = interpolate(frame, [memoFrom - 20, memoFrom, memoTo, memoTo + 30], [1, 0.3, 0.3, 1], clamp);
  const music = interpolate(frame, [0, 40, DURATION - 100, DURATION - 6], [0, 0.42, 0.42, 0], clamp) * duck;
  return (
    <AbsoluteFill>
      <Background />
      {items.map((item, i) => (
        <Sequence key={i} from={starts[i]} durationInFrames={item.duration}>
          <Transition duration={item.duration} first={i === 0} last={i === items.length - 1}>
            {item.render()}
          </Transition>
          {item.captions?.map((c, j) => <Caption key={j} {...c} />)}
          {item.whoosh && i > 0 && (
            <Sequence from={-8} durationInFrames={50} layout="none"><Audio src={staticFile("sfx/air.mp3")} volume={0.18} /></Sequence>
          )}
        </Sequence>
      ))}
      <Sequence from={memoFrom} durationInFrames={memoTo - memoFrom}>
        <Audio src={staticFile("audio/memo-14.m4a")} startFrom={Math.round((MEMO_CUT_MS / 1000) * FPS)} volume={(f) => interpolate(f, [0, 6], [0, 1], clamp)} />
      </Sequence>
      <Audio src={staticFile("audio/music.mp3")} volume={() => music} />
    </AbsoluteFill>
  );
};
