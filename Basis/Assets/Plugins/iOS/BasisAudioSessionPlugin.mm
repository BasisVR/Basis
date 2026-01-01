#import <AVFoundation/AVFoundation.h>

extern "C" {
    void BasisConfigureAudioSessionForSpeaker() {
        NSError *error = nil;
        AVAudioSession *session = [AVAudioSession sharedInstance];

        // PlayAndRecord with DefaultToSpeaker:
        // - Uses main speaker when no headphones/AirPods connected (not earpiece)
        // - Automatically switches to headphones/AirPods when connected
        // - Respects silent mode
        // - Allows microphone input
        BOOL success = [session setCategory:AVAudioSessionCategoryPlayAndRecord
                                       mode:AVAudioSessionModeVoiceChat
                                    options:AVAudioSessionCategoryOptionDefaultToSpeaker |
                                            AVAudioSessionCategoryOptionAllowBluetooth |
                                            AVAudioSessionCategoryOptionAllowBluetoothA2DP
                                      error:&error];

        if (!success) {
            NSLog(@"[BasisAudio] Failed to set audio session category: %@", error.localizedDescription);
            return;
        }

        // Activate the session
        success = [session setActive:YES error:&error];
        if (!success) {
            NSLog(@"[BasisAudio] Failed to activate audio session: %@", error.localizedDescription);
            return;
        }

        NSLog(@"[BasisAudio] Audio session configured (default to speaker, respects silent mode and external audio)");
    }

    // Re-apply audio session settings - call this if audio route gets reset unexpectedly
    void BasisReapplyAudioSession() {
        BasisConfigureAudioSessionForSpeaker();
    }
}
