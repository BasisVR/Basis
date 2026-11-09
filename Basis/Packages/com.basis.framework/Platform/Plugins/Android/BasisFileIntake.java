package com.basis.platform;

import android.app.Activity;
import android.content.ClipData;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.FileUtils;
import android.os.Parcelable;
import android.provider.OpenableColumns;
import android.util.Log;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ConcurrentLinkedQueue;

public final class BasisFileIntake {
    private static final String TAG = "BasisFileIntake";
    private static final String HANDLED = "com.basis.platform.handled";
    private static final ConcurrentLinkedQueue<String> ready = new ConcurrentLinkedQueue<>();
    private static Intent lastIntent;
    private static boolean cleaned;

    private BasisFileIntake() {}

    public static String poll(Activity activity) {
        if (activity != null) {
            Intent intent = activity.getIntent();
            if (intent != null && intent != lastIntent) {
                lastIntent = intent;
                if (!intent.getBooleanExtra(HANDLED, false)) {
                    List<Uri> uris = urisFrom(intent);
                    if (!uris.isEmpty()) {
                        intent.putExtra(HANDLED, true);
                        copyAsync(activity.getApplicationContext(), uris);
                    }
                }
            }
        }
        return ready.poll();
    }

    public static void pick(Activity activity) {
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            try {
                activity.startActivity(new Intent(activity, BasisFilePickerActivity.class));
            } catch (Exception e) {
                Log.w(TAG, "file picker unavailable", e);
            }
        });
    }

    static List<Uri> urisFrom(Intent intent) {
        List<Uri> uris = new ArrayList<>();
        String action = intent.getAction();
        if (Intent.ACTION_SEND.equals(action)) {
            Parcelable stream = intent.getParcelableExtra(Intent.EXTRA_STREAM);
            if (stream instanceof Uri) uris.add((Uri) stream);
        } else if (Intent.ACTION_SEND_MULTIPLE.equals(action)) {
            ArrayList<Parcelable> streams = intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (streams != null) for (Parcelable p : streams) if (p instanceof Uri) uris.add((Uri) p);
        } else if (Intent.ACTION_VIEW.equals(action) || action == null) {
            Uri data = intent.getData();
            if (data != null && ("content".equals(data.getScheme()) || "file".equals(data.getScheme()))) uris.add(data);
        }
        ClipData clip = intent.getClipData();
        if (uris.isEmpty() && clip != null && !Intent.ACTION_MAIN.equals(action)) {
            for (int i = 0; i < clip.getItemCount(); i++) {
                Uri uri = clip.getItemAt(i).getUri();
                if (uri != null) uris.add(uri);
            }
        }
        return uris;
    }

    static void copyAsync(Context context, List<Uri> uris) {
        new Thread(() -> {
            File root = new File(context.getCacheDir(), "basis-shared");
            synchronized (BasisFileIntake.class) {
                if (!cleaned) {
                    cleaned = true;
                    deleteTree(root);
                }
            }
            File dir = new File(root, Long.toString(System.nanoTime()));
            if (!dir.mkdirs()) return;
            StringBuilder paths = new StringBuilder();
            for (Uri uri : uris) {
                File out = new File(dir, displayName(context, uri));
                try (InputStream in = context.getContentResolver().openInputStream(uri); OutputStream os = new FileOutputStream(out)) {
                    if (in == null) continue;
                    FileUtils.copy(in, os);
                } catch (Exception e) {
                    Log.w(TAG, "could not copy " + uri, e);
                    out.delete();
                    continue;
                }
                if (paths.length() > 0) paths.append('\n');
                paths.append(out.getAbsolutePath());
            }
            if (paths.length() > 0) ready.add(paths.toString());
        }, "Basis file intake").start();
    }

    private static String displayName(Context context, Uri uri) {
        String name = null;
        try (Cursor cursor = context.getContentResolver().query(uri, new String[] { OpenableColumns.DISPLAY_NAME }, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) name = cursor.getString(0);
        } catch (Exception ignored) {
        }
        if (name == null || name.isEmpty()) name = uri.getLastPathSegment();
        if (name == null || name.isEmpty()) name = "shared";
        name = name.replace('/', '_').replace('\\', '_');
        return name.startsWith(".") ? "_" + name : name;
    }

    private static void deleteTree(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteTree(child);
        file.delete();
    }
}
