// AR7103Photos.mm
//
// Saves a PNG (the AR snapshot) to the user's photo library, then tells Unity
// how it went with UnitySendMessage(gameObject, method, result), where result is
//   "ok"       saved
//   "denied"   the user said no to photo access (now or earlier)
//   "error"    anything else
//
// Only ADD access is asked for (NSPhotoLibraryAddUsageDescription, written into
// Info.plist by Editor/IOSPostBuild.cs) -- the app never reads the library.
// Same plain-C-symbol, Objective-C++ approach as HandPoseVision.mm.

#import <UIKit/UIKit.h>
#import <Photos/Photos.h>

extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);

static void Reply(NSString* obj, NSString* method, const char* result)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        UnitySendMessage(obj.UTF8String, method.UTF8String, result);
    });
}

static void Save(NSData* data, NSString* obj, NSString* method)
{
    [[PHPhotoLibrary sharedPhotoLibrary] performChanges:^{
        PHAssetCreationRequest* req = [PHAssetCreationRequest creationRequestForAsset];
        [req addResourceWithType:PHAssetResourceTypePhoto data:data options:nil];
    } completionHandler:^(BOOL success, NSError* error) {
        Reply(obj, method, success ? "ok" : "error");
    }];
}

extern "C" void AR7103_SaveToPhotos(const unsigned char* bytes, int length, const char* gameObject, const char* method)
{
    // copy everything now: the managed buffer and strings are only valid during this call
    NSData* data = [NSData dataWithBytes:bytes length:(NSUInteger)length];
    NSString* obj = [NSString stringWithUTF8String:gameObject];
    NSString* cb = [NSString stringWithUTF8String:method];

    if (@available(iOS 14, *))
    {
        [PHPhotoLibrary requestAuthorizationForAccessLevel:PHAccessLevelAddOnly handler:^(PHAuthorizationStatus status) {
            if (status == PHAuthorizationStatusAuthorized || status == PHAuthorizationStatusLimited) Save(data, obj, cb);
            else Reply(obj, cb, "denied");
        }];
    }
    else
    {
        [PHPhotoLibrary requestAuthorization:^(PHAuthorizationStatus status) {
            if (status == PHAuthorizationStatusAuthorized) Save(data, obj, cb);
            else Reply(obj, cb, "denied");
        }];
    }
}
