package com.basis.screenshare;

import android.app.Activity;
import android.content.Intent;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;

public final class BasisScreenCaptureActivity extends Activity {
    private static final int ASK = 1;
    private boolean answered;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (savedInstanceState != null) return;
        try {
            MediaProjectionManager manager = getSystemService(MediaProjectionManager.class);
            startActivityForResult(manager.createScreenCaptureIntent(), ASK);
        } catch (Throwable e) {
            answer(RESULT_CANCELED, null, false);
            finish();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == ASK) answer(resultCode, data, true);
        finish();
    }

    @Override
    protected void onDestroy() {
        if (isFinishing()) answer(RESULT_CANCELED, null, true);
        super.onDestroy();
    }

    private void answer(int resultCode, Intent data, boolean asked) {
        if (answered) return;
        answered = true;
        BasisAndroidCapture.answered(resultCode, data, asked);
    }
}
