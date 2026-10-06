// AR7103Audio.mm
//
// Lets the app's sound play with the ring/silent switch on silent.
//
// Unity's default iOS audio session is AVAudioSessionCategoryAmbient, which the
// silent switch mutes completely -- and most people keep their phone on silent,
// so the animals and games simply made no sound. Playback is what games and
// video apps use; MixWithOthers keeps any music the person is playing going
// underneath instead of stopping it. Same plain-C-symbol approach as the
// app's other plugins. Called on scene start and whenever the app comes back
// to the foreground (iOS can reset the session while it is away).

#import <AVFoundation/AVFoundation.h>

extern "C" void AR7103_PlayEvenWhenSilenced(void)
{
    AVAudioSession *session = [AVAudioSession sharedInstance];
    NSError *error = nil;
    [session setCategory:AVAudioSessionCategoryPlayback
                    mode:AVAudioSessionModeDefault
                 options:AVAudioSessionCategoryOptionMixWithOthers
                   error:&error];
    if (error != nil) NSLog(@"[AR7103] audio session category: %@", error);
    error = nil;
    [session setActive:YES error:&error];
    if (error != nil) NSLog(@"[AR7103] audio session activate: %@", error);
}
