/*
 * ScreenCaptureKit 桥接层。
 *
 * 为什么需要它：
 *   CoreGraphics 的 CGDisplayCreateImage / CGWindowListCreateImage 在
 *   macOS 15.0 已被 **obsoleted**（见 SDK 的 SCREEN_CAPTURE_OBSOLETE 宏，
 *   头文件里明确写着 "Please use ScreenCaptureKit instead."），
 *   官方给的替代就是 ScreenCaptureKit。在 macOS 15+ 上继续用 CG 那条路
 *   是拿已移除的 API 赌运气。
 *
 * 为什么用 ObjC 桥接而不是在 C# 里直接调 Objective-C runtime：
 *   SCK 的接口是异步 block（getShareableContentWithCompletionHandler:、
 *   captureImageWithFilter:...completionHandler:）。在 C# 里手搓 objc_msgSend
 *   传 block、再自己搭同步等待，既要小心 block 的 ABI，又要处理有无返回值
 *   两种 objc_msgSend 变体，非常容易写错。写成这个几十行的 .m、
 *   暴露纯 C 接口，从 C# 侧就只是普通的 DllImport —— 桥接层是平台细节的正确归属。
 *
 * 导出的是纯 C 接口，所以不需要 ObjC 运行时反射，也不用担心 C# 侧的理解偏差。
 *
 * 编译见 build.sh：clang -fobjc-arc -dynamiclib -framework ScreenCaptureKit
 */

#import <Foundation/Foundation.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreGraphics/CoreGraphics.h>
#import <CoreMedia/CoreMedia.h>
#import <stdint.h>
#import <string.h>

/*
 * 把异步的 SCK 调用变成同步。
 *
 * 这些调用最终必然会回调一次（成功或失败），所以等信号量不会永久挂住；
 * 但仍然给了超时，避免在极端情况下（比如系统弹权限框而调用线程被卡）
 * 把整个进程拖死。超时返回一个明确的错误码，上层能分辨。
 */
static const int64_t kTimeoutNs = 15LL * NSEC_PER_SEC;

/* 错误码：与 C# 侧 BridgeError 保持一致，改一处要改两处 */
enum {
    kBridgeOK = 0,
    kBridgeTimeout = 1,
    kBridgeNoContent = 2,
    kBridgeNoDisplay = 3,
    kBridgeCaptureFailed = 4,
    kBridgeBadArgs = 5,
};

/* ------------------------------------------------------------------ 可用性 */

/*
 * SCK 的共享内容列表能不能取到 —— 也就是「屏幕录制」权限是否已授予。
 * 这是唯一可靠的判定方式：CGPreflightScreenCaptureAccess 判的是 CG 那条老路，
 * 在 15+ 上语义已经不对了。
 *
 * 用同步等待拿结果；取不到就返回 false。
 */
int remeber_sck_preflight(void)
{
    __block BOOL ok = NO;
    dispatch_semaphore_t sem = dispatch_semaphore_create(0);

    [SCShareableContent getShareableContentWithCompletionHandler:
        ^(SCShareableContent * _Nullable content, NSError * _Nullable error) {
            ok = (content != nil && error == nil);
            dispatch_semaphore_signal(sem);
        }];

    if (dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, kTimeoutNs)) != 0) {
        return 0;   /* 超时 = 当成没权限，上层会去引导授权 */
    }
    return ok ? 1 : 0;
}

/* ------------------------------------------------------------------ 抓屏 */

/*
 * 抓一块屏，返回 CGImageRef（调用方负责 CGImageRelease）。
 *
 * 参数：
 *   displayID  目标屏的 CGDirectDisplayID
 *   outW/outH  回传实际像素尺寸
 *   outErr     回传错误码
 *
 * 返回 NULL 时看 outErr：
 *   kBridgeNoContent      —— 没有屏幕录制权限，或系统拒绝了共享
 *   kBridgeNoDisplay      —— 指定 displayID 不在可共享列表里（比如屏被拔了）
 *   kBridgeCaptureFailed  —— 抓取本身失败
 *   kBridgeTimeout        —— 等待回调超时
 *
 * 注意 outW/outH 用的是 **像素**（SCStreamConfiguration.width/height），
 * 不是点 —— 上层统一按物理像素处理，和 IPlatform 的口径一致。
 */
CGImageRef remeber_sck_capture_display(uint32_t displayID, int *outW, int *outH, int *outErr)
{
    if (outErr) *outErr = kBridgeCaptureFailed;

    __block SCShareableContent *content = nil;
    dispatch_semaphore_t sem1 = dispatch_semaphore_create(0);

    [SCShareableContent getShareableContentWithCompletionHandler:
        ^(SCShareableContent * _Nullable c, NSError * _Nullable error) {
            content = c;
            dispatch_semaphore_signal(sem1);
        }];

    if (dispatch_semaphore_wait(sem1, dispatch_time(DISPATCH_TIME_NOW, kTimeoutNs)) != 0) {
        if (outErr) *outErr = kBridgeTimeout;
        return NULL;
    }
    if (content == nil) {
        if (outErr) *outErr = kBridgeNoContent;
        return NULL;
    }

    /* 找到目标屏。找不到就用主屏兜底 —— 屏被拔掉/换过之后
       displayID 会变，这时回退到主屏比直接失败有用。 */
    SCDisplay *target = nil;
    for (SCDisplay *d in content.displays) {
        if (d.displayID == displayID) { target = d; break; }
    }
    if (target == nil) {
        target = content.displays.firstObject;
    }
    if (target == nil) {
        if (outErr) *outErr = kBridgeNoDisplay;
        return NULL;
    }

    /* 用一个不含任何窗口的过滤器抓整块屏。
       用 excludingWindows:@[] 而不是 includingWindows，是为了拿到「整屏」，
       包含桌面与程序坞 —— 这是框选层需要的底图。 */
    SCContentFilter *filter =
        [[SCContentFilter alloc] initWithDisplay:target excludingWindows:@[]];

    SCStreamConfiguration *cfg = [[SCStreamConfiguration alloc] init];
    /* 用屏自身的像素尺寸，不做缩放：上层按物理像素算坐标 */
    cfg.width  = (size_t)target.width;
    cfg.height = (size_t)target.height;
    cfg.showsCursor = NO;          /* 截图里不要鼠标指针 */
    cfg.captureResolution = SCCaptureResolutionBest;
    cfg.scalesToFit = NO;

    __block CGImageRef image = NULL;
    dispatch_semaphore_t sem2 = dispatch_semaphore_create(0);

    [SCScreenshotManager captureImageWithFilter:filter
                                  configuration:cfg
                            completionHandler:^(CGImageRef _Nullable img, NSError * _Nullable error) {
        if (img != NULL && error == nil) {
            image = CGImageRetain(img);   /* 回调返回的图在回调结束后失效，必须 retain */
        }
        dispatch_semaphore_signal(sem2);
    }];

    if (dispatch_semaphore_wait(sem2, dispatch_time(DISPATCH_TIME_NOW, kTimeoutNs)) != 0) {
        if (outErr) *outErr = kBridgeTimeout;
        return NULL;
    }
    if (image == NULL) {
        if (outErr) *outErr = kBridgeCaptureFailed;
        return NULL;
    }

    if (outW) *outW = (int)CGImageGetWidth(image);
    if (outH) *outH = (int)CGImageGetHeight(image);
    if (outErr) *outErr = kBridgeOK;
    return image;
}

/*
 * 把桥接层的错误码变成一句人话，直接用在日志和给用户看的提示里。
 * 放在这里是为了让「错误码 -> 解释」只有一份，C# 侧不重复维护。
 */
const char *remeber_sck_error_text(int code)
{
    switch (code) {
        case kBridgeOK:            return "ok";
        case kBridgeTimeout:       return "等待 ScreenCaptureKit 回调超时";
        case kBridgeNoContent:     return "拿不到可共享内容（通常是缺少「屏幕录制」权限）";
        case kBridgeNoDisplay:     return "指定的显示器不在可共享列表里（可能被拔掉或编号变了）";
        case kBridgeCaptureFailed: return "ScreenCaptureKit 抓屏失败";
        case kBridgeBadArgs:       return "参数非法";
        default:                   return "未知错误";
    }
}
