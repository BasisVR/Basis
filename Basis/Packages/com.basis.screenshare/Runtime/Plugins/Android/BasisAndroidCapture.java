package com.basis.screenshare;

import android.Manifest;
import android.app.Activity;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Process;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Display;
import android.view.Surface;
import java.nio.ByteBuffer;

public final class BasisAndroidCapture {
    private static final String TAG = "BasisScreenShare";
    static final String DENIED = "screenShare.error.androidDenied";
    static final String UNAVAILABLE = "screenShare.error.androidUnavailable";
    static final String ENCODER = "screenShare.error.androidEncoder";
    static final String AUDIO_PERMISSION = "screenShare.error.androidAudioPermission";
    static final String AUDIO_FAILED = "screenShare.error.androidAudio";
    private static final String MIME = MediaFormat.MIMETYPE_VIDEO_AVC;
    private static final long LINGER_MS = 3000;
    private static final int FLAG_KEY = 1, FLAG_CONFIG = 2, AUDIO_RATE = 48000;
    private static Handler handler;
    private static Request pending, starting;
    private static boolean asking;
    private static Context context;
    private static Service service;
    private static MediaProjection projection;
    private static VirtualDisplay display;
    private static Encoder active;
    private static final Runnable linger = BasisAndroidCapture::teardown;
    private static final MediaProjection.Callback callback = new MediaProjection.Callback() {
        @Override
        public void onStop() {
            revoked();
        }
    };

    private BasisAndroidCapture() {}

    static native long nativeControl(long token);
    static native void nativeStarted(long token, int width, int height, String encoder, boolean hardware);
    static native void nativeFrame(long token, ByteBuffer data, int offset, int size, long ptsUs, int flags);
    static native void nativeAudio(long token, float[] samples, int count);
    static native void nativeAudioError(long token, String detail);
    static native void nativeEnded(long token, String detail);

    static final class Request {
        final long token;
        final int maxWidth, maxHeight, fps, kbps, keyframeMs;
        final boolean audio;

        Request(long token, int maxWidth, int maxHeight, int fps, int kbps, int keyframeMs, boolean audio) {
            this.token = token;
            this.maxWidth = maxWidth;
            this.maxHeight = maxHeight;
            this.fps = fps;
            this.kbps = kbps;
            this.keyframeMs = keyframeMs;
            this.audio = audio;
        }

        boolean alive() {
            return nativeControl(token) >= 0;
        }
    }

    static synchronized Handler handler() {
        if (handler == null) {
            HandlerThread thread = new HandlerThread("BasisScreenCapture");
            thread.start();
            handler = new Handler(thread.getLooper());
        }
        return handler;
    }

    static Activity activity() {
        try {
            return (Activity) Class.forName("com.unity3d.player.UnityPlayer").getField("currentActivity").get(null);
        } catch (Throwable e) {
            return null;
        }
    }

    public static boolean supported() {
        Activity activity = activity();
        try {
            return activity != null && activity.getSystemService(MediaProjectionManager.class) != null;
        } catch (Throwable e) {
            return false;
        }
    }

    static DisplayMetrics metrics(Context context) {
        DisplayMetrics metrics = new DisplayMetrics();
        DisplayManager displays = context.getSystemService(DisplayManager.class);
        Display main = displays != null ? displays.getDisplay(Display.DEFAULT_DISPLAY) : null;
        if (main != null) main.getRealMetrics(metrics);
        return metrics;
    }

    public static long displaySize() {
        Activity activity = activity();
        if (activity == null) return 0;
        try {
            DisplayMetrics metrics = metrics(activity);
            return ((long) Math.max(0, metrics.widthPixels) << 32) | Math.max(0, metrics.heightPixels);
        } catch (Throwable e) {
            return 0;
        }
    }

    public static boolean start(long token, int maxWidth, int maxHeight, int fps, int kbps, int keyframeMs, boolean audio) {
        Activity activity = activity();
        if (activity == null) return false;
        Request request = new Request(token, maxWidth, maxHeight, fps, kbps, keyframeMs, audio);
        handler().post(() -> begin(activity, request));
        return true;
    }

    static void begin(Activity activity, Request request) {
        context = activity.getApplicationContext();
        if (projection != null) {
            open(request);
            return;
        }
        pending = request;
        if (asking) return;
        asking = true;
        activity.runOnUiThread(() -> {
            try {
                activity.startActivity(new Intent(activity, BasisScreenCaptureActivity.class));
            } catch (Throwable e) {
                Log.w(TAG, "screen capture prompt unavailable", e);
                answered(Activity.RESULT_CANCELED, null, false);
            }
        });
    }

    static void answered(int result, Intent data, boolean asked) {
        handler().post(() -> {
            asking = false;
            Request request = pending;
            pending = null;
            if (request == null || !request.alive()) return;
            if (result != Activity.RESULT_OK || data == null) {
                nativeEnded(request.token, asked ? DENIED : UNAVAILABLE);
                return;
            }
            starting = request;
            try {
                Intent intent = new Intent(context, BasisScreenCaptureService.class).putExtra(BasisScreenCaptureService.CODE, result).putExtra(BasisScreenCaptureService.DATA, data);
                context.startForegroundService(intent);
            } catch (Throwable e) {
                Log.w(TAG, "screen capture service failed to start", e);
                starting = null;
                nativeEnded(request.token, UNAVAILABLE);
            }
        });
    }

    static void projected(Service owner, MediaProjection granted) {
        handler().post(() -> {
            Request request = starting;
            starting = null;
            if (granted == null) {
                if (request != null) nativeEnded(request.token, UNAVAILABLE);
                stopService(owner);
                return;
            }
            if (projection != null) {
                granted.stop();
                if (request != null && request.alive()) open(request);
                return;
            }
            service = owner;
            projection = granted;
            projection.registerCallback(callback, handler());
            if (request != null && request.alive()) open(request);
            else scheduleLinger();
        });
    }

    static int[] fit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight) {
        if (sourceWidth <= 0 || sourceHeight <= 0) {
            sourceWidth = maxWidth;
            sourceHeight = maxHeight;
        }
        double scale = Math.min(1.0, Math.min((double) maxWidth / sourceWidth, (double) maxHeight / sourceHeight));
        return new int[] { Math.max(2, (int) Math.round(sourceWidth * scale)) & ~1, Math.max(2, (int) Math.round(sourceHeight * scale)) & ~1 };
    }

    static void open(Request request) {
        handler().removeCallbacks(linger);
        if (active != null) active.quit = true;
        active = null;
        Encoder encoder;
        DisplayMetrics metrics;
        try {
            metrics = metrics(context);
            encoder = Encoder.create(request, fit(metrics.widthPixels, metrics.heightPixels, request.maxWidth, request.maxHeight));
        } catch (Throwable e) {
            Log.w(TAG, "screen capture encoder failed", e);
            if (display != null) display.setSurface(null);
            nativeEnded(request.token, ENCODER);
            scheduleLinger();
            return;
        }
        int dpi = Math.max(1, metrics.densityDpi);
        try {
            if (display == null) display = projection.createVirtualDisplay("Basis Screen Share", encoder.width, encoder.height, dpi, DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, encoder.surface, null, handler());
            else {
                display.resize(encoder.width, encoder.height, dpi);
                display.setSurface(encoder.surface);
            }
        } catch (Throwable e) {
            Log.w(TAG, "screen capture display failed", e);
            display = null;
        }
        if (display == null) {
            encoder.release();
            nativeEnded(request.token, UNAVAILABLE);
            teardown();
            return;
        }
        active = encoder;
        nativeStarted(request.token, encoder.width, encoder.height, encoder.name, encoder.hardware);
        encoder.start();
        if (request.audio) encoder.startAudio(context, projection);
    }

    static void finished(Encoder encoder) {
        if (active == encoder) {
            active = null;
            if (display != null) display.setSurface(null);
            scheduleLinger();
        }
        encoder.release();
    }

    static void scheduleLinger() {
        handler().removeCallbacks(linger);
        handler().postDelayed(linger, LINGER_MS);
    }

    static void teardown() {
        handler().removeCallbacks(linger);
        if (active != null || asking || starting != null) return;
        MediaProjection ending = projection;
        projection = null;
        if (display != null) display.release();
        display = null;
        if (ending != null) {
            ending.unregisterCallback(callback);
            ending.stop();
        }
        stopService(service);
        service = null;
    }

    static void revoked() {
        if (projection == null) return;
        projection = null;
        Encoder encoder = active;
        active = null;
        if (encoder != null) {
            encoder.quit = true;
            nativeEnded(encoder.token, "");
        }
        if (display != null) display.release();
        display = null;
        stopService(service);
        service = null;
    }

    static void stopService(Service owner) {
        if (owner == null) return;
        try {
            owner.stopForeground(Service.STOP_FOREGROUND_REMOVE);
            owner.stopSelf();
        } catch (Throwable e) {
            Log.w(TAG, "screen capture service stop failed", e);
        }
    }

    static int level(int width, int height, int fps) {
        long frame = (long) ((width + 15) / 16) * ((height + 15) / 16), rate = frame * fps;
        if (frame <= 1620 && rate <= 40500) return MediaCodecInfo.CodecProfileLevel.AVCLevel3;
        if (frame <= 3600 && rate <= 108000) return MediaCodecInfo.CodecProfileLevel.AVCLevel31;
        if (frame <= 5120 && rate <= 216000) return MediaCodecInfo.CodecProfileLevel.AVCLevel32;
        if (frame <= 8192 && rate <= 245760) return MediaCodecInfo.CodecProfileLevel.AVCLevel41;
        if (frame <= 8704 && rate <= 522240) return MediaCodecInfo.CodecProfileLevel.AVCLevel42;
        if (frame <= 22080 && rate <= 589824) return MediaCodecInfo.CodecProfileLevel.AVCLevel5;
        if (frame <= 36864 && rate <= 983040) return MediaCodecInfo.CodecProfileLevel.AVCLevel51;
        return MediaCodecInfo.CodecProfileLevel.AVCLevel52;
    }

    static final class Encoder implements Runnable {
        final long token;
        final MediaCodec codec;
        final Surface surface;
        final int width, height;
        final String name;
        final boolean hardware;
        volatile boolean quit;
        int bitrate;
        Thread drain, audio;

        private Encoder(long token, MediaCodec codec, int width, int height, int kbps) {
            this.token = token;
            this.codec = codec;
            this.width = width;
            this.height = height;
            this.bitrate = kbps;
            name = codec.getName();
            hardware = codec.getCodecInfo().isHardwareAccelerated();
            surface = codec.createInputSurface();
        }

        static int[] supportedSize(MediaCodecInfo.VideoCapabilities video, int[] size) {
            int widthAlign = Math.max(2, video.getWidthAlignment()), heightAlign = Math.max(2, video.getHeightAlignment());
            int width = size[0], height = size[1];
            for (int i = 0; i < 8; i++) {
                int w = Math.max(widthAlign, width / widthAlign * widthAlign), h = Math.max(heightAlign, height / heightAlign * heightAlign);
                if (video.isSizeSupported(w, h)) return new int[] { w, h };
                width = width * 3 / 4;
                height = height * 3 / 4;
            }
            throw new IllegalArgumentException("no supported encoder size for " + size[0] + "x" + size[1]);
        }

        static Encoder create(Request request, int[] fitted) throws Exception {
            MediaCodec codec = MediaCodec.createEncoderByType(MIME);
            try {
                MediaCodecInfo.CodecCapabilities caps = codec.getCodecInfo().getCapabilitiesForType(MIME);
                int[] size = supportedSize(caps.getVideoCapabilities(), fitted);
                int fps = Math.max(1, request.fps), kbps = Math.max(100, request.kbps);
                int profile = MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline, maxLevel = 0;
                for (MediaCodecInfo.CodecProfileLevel entry : caps.profileLevels) if (entry.profile == MediaCodecInfo.CodecProfileLevel.AVCProfileConstrainedBaseline) profile = entry.profile;
                for (MediaCodecInfo.CodecProfileLevel entry : caps.profileLevels) if (entry.profile == profile) maxLevel = Math.max(maxLevel, entry.level);
                int level = maxLevel > 0 ? Math.min(level(size[0], size[1], fps), maxLevel) : level(size[0], size[1], fps);
                int[][] attempts = { { profile, level }, { MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline, level }, { 0, 0 } };
                Exception last = null;
                for (int[] attempt : attempts) {
                    MediaFormat format = MediaFormat.createVideoFormat(MIME, size[0], size[1]);
                    format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
                    format.setInteger(MediaFormat.KEY_BIT_RATE, kbps * 1000);
                    format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
                    format.setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, fps);
                    format.setFloat(MediaFormat.KEY_I_FRAME_INTERVAL, Math.max(1000, request.keyframeMs) / 1000f);
                    format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 500_000L);
                    format.setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1);
                    format.setInteger(MediaFormat.KEY_PRIORITY, 0);
                    format.setInteger(MediaFormat.KEY_LATENCY, 1);
                    if (attempt[0] != 0) {
                        format.setInteger(MediaFormat.KEY_PROFILE, attempt[0]);
                        format.setInteger(MediaFormat.KEY_LEVEL, attempt[1]);
                    }
                    try {
                        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                        return new Encoder(request.token, codec, size[0], size[1], kbps);
                    } catch (IllegalArgumentException | IllegalStateException e) {
                        last = e;
                        codec.reset();
                    }
                }
                throw last;
            } catch (Exception e) {
                codec.release();
                throw e;
            }
        }

        void start() {
            codec.start();
            drain = new Thread(this, "BasisScreenEncode");
            drain.start();
        }

        void request(String key, int value) {
            Bundle parameters = new Bundle();
            parameters.putInt(key, value);
            codec.setParameters(parameters);
        }

        @Override
        public void run() {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            String failure = null;
            try {
                while (!quit) {
                    long control = nativeControl(token);
                    if (control < 0) break;
                    int kbps = (int) (control & 0xffffffffL);
                    if (kbps > 0 && kbps != bitrate) {
                        request(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, kbps * 1000);
                        bitrate = kbps;
                    }
                    if (((control >>> 32) & 1) != 0) request(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                    int index = codec.dequeueOutputBuffer(info, 20_000);
                    if (index < 0) continue;
                    ByteBuffer out = codec.getOutputBuffer(index);
                    if (out != null && info.size > 0) {
                        int flags = ((info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0 ? FLAG_KEY : 0) | ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0 ? FLAG_CONFIG : 0);
                        nativeFrame(token, out, info.offset, info.size, info.presentationTimeUs, flags);
                    }
                    codec.releaseOutputBuffer(index, false);
                    if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
                }
            } catch (Throwable e) {
                if (!quit) {
                    Log.w(TAG, "screen capture encoder stopped", e);
                    failure = ENCODER;
                }
            }
            quit = true;
            if (failure != null) nativeEnded(token, failure);
            handler().post(() -> finished(this));
        }

        void startAudio(Context context, MediaProjection projection) {
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                nativeAudioError(token, AUDIO_PERMISSION);
                return;
            }
            AudioRecord record;
            try {
                AudioPlaybackCaptureConfiguration config = new AudioPlaybackCaptureConfiguration.Builder(projection).addMatchingUsage(AudioAttributes.USAGE_MEDIA).addMatchingUsage(AudioAttributes.USAGE_GAME).addMatchingUsage(AudioAttributes.USAGE_UNKNOWN).excludeUid(Process.myUid()).build();
                AudioFormat format = new AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_FLOAT).setSampleRate(AUDIO_RATE).setChannelMask(AudioFormat.CHANNEL_IN_STEREO).build();
                int minimum = AudioRecord.getMinBufferSize(AUDIO_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_FLOAT);
                record = new AudioRecord.Builder().setAudioFormat(format).setBufferSizeInBytes(Math.max(minimum, AUDIO_RATE * 2 * 4 / 5)).setAudioPlaybackCaptureConfig(config).build();
                if (record.getState() != AudioRecord.STATE_INITIALIZED) {
                    record.release();
                    throw new IllegalStateException("playback capture did not initialise");
                }
                record.startRecording();
            } catch (Throwable e) {
                Log.w(TAG, "screen capture audio failed", e);
                nativeAudioError(token, AUDIO_FAILED);
                return;
            }
            audio = new Thread(() -> {
                float[] samples = new float[1920];
                try {
                    while (!quit) {
                        int read = record.read(samples, 0, samples.length, AudioRecord.READ_BLOCKING);
                        if (read < 0) {
                            if (!quit) nativeAudioError(token, AUDIO_FAILED);
                            break;
                        }
                        if (read > 0 && !quit) nativeAudio(token, samples, read);
                    }
                } catch (Throwable e) {
                    if (!quit) nativeAudioError(token, AUDIO_FAILED);
                }
                try {
                    record.stop();
                } catch (Throwable ignored) {
                }
                record.release();
            }, "BasisScreenAudio");
            audio.start();
        }

        void release() {
            quit = true;
            try {
                codec.stop();
            } catch (Throwable ignored) {
            }
            codec.release();
            surface.release();
        }
    }
}
