import { Composition } from "remotion";
import { Demo, DURATION } from "./Demo";
import { FPS, HEIGHT, WIDTH } from "./engine/shot";

export const Root: React.FC = () => (
  <Composition id="Demo" component={Demo} durationInFrames={DURATION} fps={FPS} width={WIDTH} height={HEIGHT} />
);
