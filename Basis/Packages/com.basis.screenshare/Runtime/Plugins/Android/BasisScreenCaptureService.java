package com.basis.screenshare;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.IBinder;
import android.util.Log;

public final class BasisScreenCaptureService extends Service {
    static final String CODE = "com.basis.screenshare.code", DATA = "com.basis.screenshare.data";
    private static final String CHANNEL = "basis_screen_share";
    private static final int NOTIFICATION = 0x5c5e;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        MediaProjection projection = null;
        try {
            CharSequence label = getApplicationInfo().loadLabel(getPackageManager());
            NotificationManager notifications = getSystemService(NotificationManager.class);
            if (notifications != null) notifications.createNotificationChannel(new NotificationChannel(CHANNEL, label, NotificationManager.IMPORTANCE_LOW));
            int icon = getApplicationInfo().icon != 0 ? getApplicationInfo().icon : android.R.drawable.ic_menu_share;
            Notification notification = new Notification.Builder(this, CHANNEL).setSmallIcon(icon).setContentTitle(label).setOngoing(true).build();
            startForeground(NOTIFICATION, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
            Intent data = intent != null ? intent.getParcelableExtra(DATA) : null;
            MediaProjectionManager manager = getSystemService(MediaProjectionManager.class);
            if (data != null && manager != null) projection = manager.getMediaProjection(intent.getIntExtra(CODE, 0), data);
        } catch (Throwable e) {
            Log.w("BasisScreenShare", "screen capture projection failed", e);
        }
        BasisAndroidCapture.projected(this, projection);
        return START_NOT_STICKY;
    }
}
