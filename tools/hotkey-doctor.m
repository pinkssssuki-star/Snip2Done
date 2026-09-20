/*
 * 全局热键诊断工具（独立最小程序，不依赖记忆面板的任何代码）。
 *
 * 用途：把「热键不工作」这件事从 app 里隔离出来，一次跑完就给出定论 ——
 *   1. 当前各项输入权限的授权状态
 *   2. Carbon RegisterEventHotKey 是否注册成功
 *   3. 按下 ⌘⇧A 时事件到底有没有派发进来
 *   4. 同时装一个 NSEvent 全局监听作为对照（两条路的差别一目了然）
 *
 * 编译：clang -O1 -o hotkey-doctor hotkey-doctor.m -framework Carbon -framework AppKit
 */
#import <Carbon/Carbon.h>
#import <AppKit/AppKit.h>
#import <ApplicationServices/ApplicationServices.h>
#include <stdio.h>

static int carbonHits = 0;
static int nsHits = 0;

static OSStatus carbonHandler(EventHandlerCallRef next, EventRef ev, void *ud) {
    EventHotKeyID id;
    GetEventParameter(ev, kEventParamDirectObject, typeEventHotKeyID, NULL,
                      sizeof(id), NULL, &id);
    carbonHits++;
    printf("  ★ Carbon 收到热键！id=%u\n", (unsigned)id.id); fflush(stdout);
    return noErr;
}

int main(void) {
    printf("══════════════════════════════════════════════\n");
    printf("  全局热键诊断\n");
    printf("══════════════════════════════════════════════\n\n");

    printf("【1】当前权限状态\n");
    printf("    辅助功能   AXIsProcessTrusted            = %d  %s\n",
           AXIsProcessTrusted(), AXIsProcessTrusted() ? "已授权" : "未授权");
    printf("    输入监控   CGPreflightListenEventAccess  = %d  %s\n",
           CGPreflightListenEventAccess(),
           CGPreflightListenEventAccess() ? "已授权" : "未授权");
    printf("    屏幕录制   CGPreflightScreenCaptureAccess= %d  %s\n\n",
           CGPreflightScreenCaptureAccess(),
           CGPreflightScreenCaptureAccess() ? "已授权" : "未授权");

    printf("【2】Carbon 后端\n");
    RunCurrentEventLoop(kEventDurationSecond);
    EventTypeSpec spec[2] = {
        { kEventClassKeyboard, kEventHotKeyPressed },
        { kEventClassKeyboard, kEventHotKeyReleased },
    };
    OSStatus ie = InstallEventHandler(GetApplicationEventTarget(), carbonHandler, 2, spec, NULL, NULL);
    EventHotKeyRef ref = NULL;
    EventHotKeyID hk = { 'DOCT', 1 };
    OSStatus re = RegisterEventHotKey(0, cmdKey | shiftKey, hk,
                                      GetApplicationEventTarget(), 0, &ref);
    printf("    InstallEventHandler    -> err=%d\n", (int)ie);
    printf("    RegisterEventHotKey    -> err=%d  ref=%p  %s\n\n",
           (int)re, (void *)ref, re == 0 ? "（注册成功）" : "（注册失败）");

    printf("【3】NSEvent 后端（监听式，需要「输入监控」）\n");
    id monitor = [NSEvent addGlobalMonitorForEventsMatchingMask:NSEventMaskKeyDown
                                                       handler:^(NSEvent *e) {
        NSEventModifierFlags f = e.modifierFlags & NSEventModifierFlagDeviceIndependentFlagsMask;
        BOOL want = (f & NSEventModifierFlagCommand) && (f & NSEventModifierFlagShift);
        if (f == (NSEventModifierFlagCommand | NSEventModifierFlagShift)) {
            if (e.keyCode == 0) {
                nsHits++;
                printf("  ★ NSEvent 收到 ⌘⇧A！\n"); fflush(stdout);
            }
        }
        (void)want;
    }];
    printf("    addGlobalMonitor 返回     = %s\n",
           monitor != nil ? "非 nil（注意：没授权时它也可能非 nil，事件却不到）" : "nil");
    printf("\n");

    printf("══════════════════════════════════════════════\n");
    printf("  请现在按 ⌘⇧A（Command + Shift + A）5 次\n");
    printf("  20 秒后自动结束\n");
    printf("══════════════════════════════════════════════\n");
    fflush(stdout);

    [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:20.0]];

    printf("\n【结果】\n");
    printf("    Carbon 收到 %d 次\n", carbonHits);
    printf("    NSEvent 收到 %d 次\n\n", nsHits);

    if (carbonHits > 0 || nsHits > 0) {
        printf("    → 热键机制在这台机器上是通的。若面板里仍不工作，问题在面板那边。\n");
    } else {
        printf("    → 两条路都收不到按键。\n");
        printf("      这是**权限问题**，不是代码问题：\n");
        if (!AXIsProcessTrusted())
            printf("        · 给「辅助功能」授权（Carbon 热键需要）\n");
        if (!CGPreflightListenEventAccess())
            printf("        · 给「输入监控」授权（NSEvent 监听需要）\n");
        printf("      授权对象：你运行本程序的终端（或 .app 自己），授权后**必须重启它**。\n");
    }
    return 0;
}
