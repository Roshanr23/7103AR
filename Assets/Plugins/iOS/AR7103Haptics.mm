// AR7103Haptics.mm
//
// Taptic Engine feedback for the AR scenes, exposed to Unity as a plain C
// symbol (same approach as HandPoseVision.mm: Objective-C++, no bridging).
//
//   kind 0 = light tap   (palm recognised, button press)
//   kind 1 = medium tap  (floor locked)
//   kind 2 = success     (the animal has arrived)
//   kind 3 = selection   (a small tick: carousel page change)
//
// UIKit feedback generators must be used on the main thread; Unity calls
// plugins from its player loop on the main thread on iOS, and the dispatch is
// a guard for anything else. Devices without a Taptic Engine just do nothing.

#import <UIKit/UIKit.h>

extern "C" void AR7103_Haptic(int kind)
{
    void (^fire)(void) = ^{
        switch (kind)
        {
            case 0: { UIImpactFeedbackGenerator *g = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleLight];
                      [g prepare]; [g impactOccurred]; break; }
            case 1: { UIImpactFeedbackGenerator *g = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleMedium];
                      [g prepare]; [g impactOccurred]; break; }
            case 2: { UINotificationFeedbackGenerator *g = [[UINotificationFeedbackGenerator alloc] init];
                      [g prepare]; [g notificationOccurred:UINotificationFeedbackTypeSuccess]; break; }
            default: { UISelectionFeedbackGenerator *g = [[UISelectionFeedbackGenerator alloc] init];
                       [g prepare]; [g selectionChanged]; break; }
        }
    };
    if ([NSThread isMainThread]) fire(); else dispatch_async(dispatch_get_main_queue(), fire);
}
