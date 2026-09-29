// HandPoseVision.mm
//
// Apple Vision hand-pose detection, exposed to Unity as plain C symbols.
//
// Written as Objective-C++ (.mm) rather than Swift on purpose: Unity's iOS
// build pipeline compiles .mm files in the generated Xcode project directly,
// with no bridging header, module map or Swift-version pinning to go wrong.
//
// The detection call is SYNCHRONOUS. Vision on a downscaled grayscale frame is
// a few ms on any device that can run Vuforia, and doing it inline avoids
// marshalling results back across a dispatch queue into the managed heap --
// which is where this kind of plugin usually crashes.
//
// Requires iOS 14+ for VNDetectHumanHandPoseRequest. On anything older,
// HandPose_IsSupported() returns 0 and detection is a no-op.

#import <Foundation/Foundation.h>
#import <Vision/Vision.h>
#import <CoreGraphics/CoreGraphics.h>

// 21 joints per hand, 3 floats each (x, y, confidence)
static const int kJointsPerHand = 21;
static const int kFloatsPerJoint = 3;
static const int kFloatsPerHand = kJointsPerHand * kFloatsPerJoint;

// Joint order handed back to C#. Must match HandJoint in HandPoseProvider.cs.
//
// Typed as plain NSString* rather than VNHumanHandPoseObservationJointName so
// the declaration itself carries no iOS-14 availability requirement -- that
// typedef is API_AVAILABLE(ios(14.0)), and annotating a file-scope static is
// awkward. The joint-name constants are the same NSString values underneath.
static NSString *kJointOrder[kJointsPerHand];
static BOOL kJointOrderReady = NO;

// API_AVAILABLE is required even though every caller is already inside an
// @available check: clang cannot see through the function boundary, and
// -Wunguarded-availability-new is promoted to an error in many Xcode configs.
API_AVAILABLE(ios(14.0))
static void InitJointOrder(void) {
    if (kJointOrderReady) return;
    int i = 0;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameWrist;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameThumbCMC;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameThumbMP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameThumbIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameThumbTip;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameIndexMCP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameIndexPIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameIndexDIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameIndexTip;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameMiddleMCP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameMiddlePIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameMiddleDIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameMiddleTip;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameRingMCP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameRingPIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameRingDIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameRingTip;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameLittleMCP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameLittlePIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameLittleDIP;
    kJointOrder[i++] = VNHumanHandPoseObservationJointNameLittleTip;
    kJointOrderReady = YES;
}

// Build a CGImage from a tightly-packed grayscale buffer.
static CGImageRef CreateGrayImage(const uint8_t *bytes, int width, int height) {
    CGColorSpaceRef cs = CGColorSpaceCreateDeviceGray();
    CGContextRef ctx = CGBitmapContextCreate((void *)bytes, width, height,
                                             8, width, cs,
                                             (CGBitmapInfo)kCGImageAlphaNone);
    CGImageRef img = ctx ? CGBitmapContextCreateImage(ctx) : NULL;
    if (ctx) CGContextRelease(ctx);
    CGColorSpaceRelease(cs);
    return img;
}

// Build a CGImage from tightly-packed RGB888.
static CGImageRef CreateRGBImage(const uint8_t *bytes, int width, int height) {
    CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
    CGDataProviderRef provider =
        CGDataProviderCreateWithData(NULL, bytes, (size_t)width * height * 3, NULL);
    CGImageRef img = CGImageCreate(width, height, 8, 24, (size_t)width * 3, cs,
                                   (CGBitmapInfo)kCGImageAlphaNone, provider,
                                   NULL, false, kCGRenderingIntentDefault);
    CGDataProviderRelease(provider);
    CGColorSpaceRelease(cs);
    return img;
}

extern "C" {

int HandPose_IsSupported(void) {
    if (@available(iOS 14.0, *)) { return 1; }
    return 0;
}

/// Detect hands in a raw camera frame.
///
/// bytes        tightly-packed pixel data
/// width/height frame dimensions
/// channels     1 = grayscale, 3 = RGB888
/// orientation  CGImagePropertyOrientation (1 = up, 6 = right, etc.)
/// maxHands     how many hands to look for
/// outJoints    caller-allocated float buffer, maxHands * 63 floats
/// outChirality caller-allocated int buffer, maxHands ints (0 unknown, 1 left, 2 right)
///
/// Returns the number of hands written, or a negative value on error.
int HandPose_Detect(const uint8_t *bytes, int width, int height, int channels,
                    int orientation, int maxHands,
                    float *outJoints, int *outChirality) {
    if (@available(iOS 14.0, *)) {
        if (!bytes || !outJoints || width <= 0 || height <= 0 || maxHands <= 0) return -1;
        if (channels != 1 && channels != 3) return -2;

        InitJointOrder();

        CGImageRef img = (channels == 1) ? CreateGrayImage(bytes, width, height)
                                         : CreateRGBImage(bytes, width, height);
        if (!img) return -3;

        VNDetectHumanHandPoseRequest *req = [[VNDetectHumanHandPoseRequest alloc] init];
        req.maximumHandCount = maxHands;

        VNImageRequestHandler *handler =
            [[VNImageRequestHandler alloc] initWithCGImage:img
                                               orientation:(CGImagePropertyOrientation)orientation
                                                   options:@{}];
        NSError *err = nil;
        BOOL ok = [handler performRequests:@[req] error:&err];
        CGImageRelease(img);
        if (!ok || err) return -4;

        NSArray<VNHumanHandPoseObservation *> *obs = req.results;
        int handCount = (int)MIN((NSUInteger)maxHands, obs.count);

        for (int h = 0; h < handCount; h++) {
            VNHumanHandPoseObservation *o = obs[h];
            float *dst = outJoints + (h * kFloatsPerHand);

            if (outChirality) {
                // chirality is iOS 15+, one version LATER than the hand-pose
                // request itself (iOS 14). On iOS 14 we still detect hands, we
                // just cannot say which hand it is.
                if (@available(iOS 15.0, *)) {
                    switch (o.chirality) {
                        case VNChiralityLeft:  outChirality[h] = 1; break;
                        case VNChiralityRight: outChirality[h] = 2; break;
                        default:               outChirality[h] = 0; break;
                    }
                } else {
                    outChirality[h] = 0;   // unknown
                }
            }

            for (int j = 0; j < kJointsPerHand; j++) {
                NSError *jerr = nil;
                VNRecognizedPoint *p = [o recognizedPointForJointName:
                                            (VNHumanHandPoseObservationJointName)kJointOrder[j]
                                                                error:&jerr];
                int base = j * kFloatsPerJoint;
                if (p && !jerr) {
                    // Vision returns normalised coords, origin bottom-left.
                    dst[base + 0] = (float)p.location.x;
                    dst[base + 1] = (float)p.location.y;
                    dst[base + 2] = (float)p.confidence;
                } else {
                    dst[base + 0] = 0.0f;
                    dst[base + 1] = 0.0f;
                    dst[base + 2] = 0.0f;   // zero confidence = treat as missing
                }
            }
        }
        return handCount;
    }
    return -100;   // iOS < 14
}

}  // extern "C"
