import { Config } from "@remotion/cli/config";

Config.setVideoImageFormat("jpeg");
Config.setJpegQuality(95);
Config.setCodec("h264");
Config.setCrf(16);
Config.setPixelFormat("yuv420p");
Config.setConcurrency(null);
// The takes are 4:4:4 H.264; decode them off-thread with the bundled FFmpeg.
Config.setOffthreadVideoCacheSizeInBytes(2 * 1024 * 1024 * 1024);
