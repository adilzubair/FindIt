import { color } from "../theme";

/** A Windows-style arrow pointer with a click ripple, drawn at output pixels. */
export const Cursor: React.FC<{ x: number; y: number; press: number; ripple: number; opacity?: number }> = ({ x, y, press, ripple, opacity = 1 }) => {
  const size = 1.55 * (1 - press * 0.14);
  const r = 8 + ripple * 34;
  return (
    <>
      {ripple >= 0 && (
        <div
          style={{
            position: "absolute",
            left: x - r,
            top: y - r,
            width: 2 * r,
            height: 2 * r,
            borderRadius: "50%",
            background: color.accent,
            opacity: (1 - ripple) * 0.45 * opacity,
          }}
        />
      )}
      <svg
        width={40}
        height={44}
        viewBox="0 0 20 22"
        style={{ position: "absolute", left: x - 2, top: y - 2, transform: `scale(${size})`, transformOrigin: "2px 2px", opacity, filter: "drop-shadow(0 3px 5px rgba(0,0,0,0.45))", overflow: "visible" }}
      >
        <path d="M2 2 L2 17.5 L6 13.8 L8.7 19.6 L11.3 18.5 L8.7 12.8 L14 12.8 Z" fill="#fff" stroke="#111" strokeWidth={1.1} strokeLinejoin="round" />
      </svg>
    </>
  );
};
