'use strict';

/**
 * 取「用户此刻在看哪个窗口」的进程名与标题 —— 纯图片原则下唯一保留的免费文本。
 *
 * ⚠️ 为什么不用 GetForegroundWindow：
 *   实测（用户机器，2026-09）发现微信 4.x 会有一个隐藏窗口长期占据「前台」，
 *   于是无论在哪里截图，GetForegroundWindow 都返回 Weixin|微信。
 *   而用户按快捷键时，鼠标几乎总停在他想截的那个窗口上，
 *   所以改用 WindowFromPoint + GetAncestor(GA_ROOT)：「鼠标底下是谁」。
 *   万一鼠标停在桌面/任务栏（无标题窗口），再退回枚举 Z 序窗口，最后才用前台窗口兜底。
 *
 * ⚠️ 必须在我们的 overlay / chooser 窗口出现「之前」采样：
 *   我们的框选层是全屏顶层窗口，一旦出现，WindowFromPoint 只会返回它自己。
 *
 * ⚠️ 结果不走 stdout，而是写成 UTF-8 文件。
 *   Windows PowerShell 5.1 往管道写字符串用的是系统 ANSI 码页（中文机器 = GBK），
 *   Node 按 UTF-8 解码就会得到乱码（踩过：窗口标题在界面上显示成一堆 ���）。
 *   写文件时可以显式指定编码，彻底绕开码页问题。
 *
 * ⚠️ 所有 Win32 调用都写在 C# 里：
 *   在 PowerShell 里用 [ref] 传 struct 参数会静默失败（踩过：返回空窗口）。
 */

const { execFile } = require('child_process');
const fs = require('fs');
const path = require('path');

const PS_SCRIPT = `
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class WinProbe {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  delegate bool EnumProc(IntPtr h, IntPtr l);

  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

  static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "";
    var sb = new StringBuilder(1024);
    GetWindowTextW(h, sb, 1024);
    uint pid;
    GetWindowThreadProcessId(h, out pid);
    string name = "";
    try { name = Process.GetProcessById((int)pid).ProcessName; } catch {}
    string t = sb.ToString().Replace("\\r", " ").Replace("\\n", " ").Replace("|", " ").Trim();
    return name + "|" + t;
  }

  static List<string> _hits;
  static int _px, _py;

  static bool OnWin(IntPtr h, IntPtr l) {
    if (!IsWindowVisible(h) || IsIconic(h)) return true;
    RECT r;
    if (!GetWindowRect(h, out r)) return true;
    if (_px < r.Left || _px >= r.Right || _py < r.Top || _py >= r.Bottom) return true;
    string d = Describe(h);
    if (string.IsNullOrEmpty(d) || d.EndsWith("|")) return true;
    _hits.Add(d);
    return true;
  }

  // EnumWindows walks top-to-bottom in z-order, so the first hit is the one on screen.
  static string AtPoint(int x, int y) {
    _px = x; _py = y; _hits = new List<string>();
    EnumWindows(OnWin, IntPtr.Zero);
    return _hits.Count > 0 ? _hits[0] : "";
  }

  public static string Probe() {
    POINT p;
    GetCursorPos(out p);

    string under = Describe(GetAncestor(WindowFromPoint(p), 2));
    string fg = Describe(GetForegroundWindow());

    // Prefer what is under the cursor, then enumeration, then the foreground window.
    string pick = under;
    if (string.IsNullOrEmpty(pick) || pick.EndsWith("|")) pick = AtPoint(p.X, p.Y);
    if (string.IsNullOrEmpty(pick)) pick = fg;

    return pick + "\\u0001" + under + "\\u0001" + fg + "\\u0001" + p.X + "," + p.Y;
  }
}
"@

# 不再静默失败：编译不出来就明确写出来，好让上层能看见
if (([System.Management.Automation.PSTypeName]'WinProbe').Type) {
  $out = [WinProbe]::Probe()
} else {
  $out = 'ERR|AddTypeFailed|||'
}

if ($args.Count -ge 1) {
  # 显式 UTF-8 写文件（无 BOM），绕开控制台码页
  [System.IO.File]::WriteAllText($args[0], $out, (New-Object System.Text.UTF8Encoding($false)))
} else {
  $out
}
`;

let scriptPath = null;

function ensureScript(dataDir) {
  if (scriptPath) return scriptPath;
  const p = path.join(dataDir, 'winprobe.ps1');
  try {
    // ⚠️ 必须带 UTF-8 BOM：PowerShell 5.1 读无 BOM 的 .ps1 会按 GBK 解释
    const BOM = Buffer.from([0xef, 0xbb, 0xbf]);
    fs.writeFileSync(p, Buffer.concat([BOM, Buffer.from(PS_SCRIPT, 'utf8')]));
    scriptPath = p;
  } catch (_) {
    scriptPath = null;
  }
  return scriptPath;
}

function parseOne(s) {
  const v = String(s || '');
  const i = v.indexOf('|');
  if (i < 0) return { source: '', title: '' };
  return { source: v.slice(0, i).trim(), title: v.slice(i + 1).trim() };
}

/**
 * @param {string} dataDir
 * @param {number} [timeoutMs]
 * @param {(msg:string)=>void} [onDebug]
 * @returns {Promise<{title:string, source:string}>} 永不 reject
 */
function getForeground(dataDir, timeoutMs = 3000, onDebug) {
  return new Promise((resolve) => {
    const empty = { title: '', source: '' };
    const script = ensureScript(dataDir);
    if (!script) return resolve(empty);

    const outFile = path.join(dataDir, 'winprobe.out');
    try {
      fs.rmSync(outFile, { force: true });
    } catch (_) {}

    let settled = false;
    const finish = (v) => {
      if (!settled) {
        settled = true;
        resolve(v);
      }
    };

    let child;
    try {
      child = execFile(
        'powershell.exe',
        [
          '-NoProfile',
          '-NonInteractive',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          script,
          outFile,
        ],
        { timeout: timeoutMs, windowsHide: true, maxBuffer: 1024 * 64 },
        () => {
          // 不看 stdout（码页不可靠），只读文件
          let raw = '';
          try {
            raw = fs.readFileSync(outFile, 'utf8');
          } catch (_) {
            return finish(empty);
          }

          const line = raw.split(/\r?\n/).filter(Boolean)[0] || '';
          const parts = line.split('\u0001');
          const picked = parseOne(parts[0]);
          const under = parseOne(parts[1]);
          const fg = parseOne(parts[2]);

          if (picked.source === 'ERR') {
            onDebug && onDebug('窗口探测失败：PowerShell 里的 Add-Type 没编译出来');
            return finish(empty);
          }

          if (onDebug) {
            onDebug(
              `窗口探测 | 采用 ${picked.source}/${picked.title} | ` +
                `鼠标下 ${under.source}/${under.title} | 前台 ${fg.source}/${fg.title} | 光标 ${parts[3] || '?'}`
            );
          }
          finish(picked);
        }
      );
    } catch (_) {
      return finish(empty);
    }

    // 双保险：即使 execFile 不回调也不卡住截图流程
    setTimeout(() => {
      try {
        if (child) child.kill();
      } catch (_) {}
      finish(empty);
    }, timeoutMs + 250);
  });
}

/**
 * 关掉某个窗口的 DWM 过渡动画。
 *
 * 为什么需要：框选层是全屏顶层窗口，我们从各个层面都量过 ——
 * 窗口几何、不透明度、渲染视口、图片解码状态在显示后第一毫秒就全部稳定，
 * 但用户仍然看到一下「轻微放大」的过渡。剩下的唯一嫌疑就是
 * DWM（桌面窗口管理器）自己对窗口首次呈现做的动画。
 * DWMWA_TRANSITIONS_FORCEDISABLED(3) 就是 Windows 为此提供的开关。
 *
 * @param {string} dataDir
 * @param {Buffer|number} hwnd win.getNativeWindowHandle() 的返回值
 * @param {(msg:string)=>void} [onDebug]
 */
function disableTransitions(dataDir, hwnd, onDebug) {
  return new Promise((resolve) => {
    let h = 0;
    try {
      h = Buffer.isBuffer(hwnd)
        ? Number(hwnd.readBigUInt64LE ? hwnd.readBigUInt64LE(0) : hwnd.readUInt32LE(0))
        : Number(hwnd);
    } catch (_) {
      return resolve(false);
    }
    if (!h) return resolve(false);

    const file = path.join(dataDir, 'dwm.ps1');
    try {
      const BOM = Buffer.from([0xef, 0xbb, 0xbf]);
      fs.writeFileSync(file, Buffer.concat([BOM, Buffer.from(DWM_SCRIPT, 'utf8')]));
    } catch (_) {
      return resolve(false);
    }

    let settled = false;
    const done = (v) => {
      if (!settled) {
        settled = true;
        resolve(v);
      }
    };

    try {
      execFile(
        'powershell.exe',
        [
          '-NoProfile',
          '-NonInteractive',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          file,
          String(h),
        ],
        { timeout: 8000, windowsHide: true },
        (err, stdout) => {
          const out = String(stdout || '').trim();
          if (err) {
            onDebug && onDebug('DWM 关闭过渡动画失败：' + String(err.message || err));
            return done(false);
          }
          onDebug && onDebug('DWM TRANSITIONS_FORCEDISABLED 返回 hr=' + out);
          done(out.startsWith('0'));
        }
      );
    } catch (_) {
      done(false);
    }
  });
}

const DWM_SCRIPT = `
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class DshDwm {
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
}
"@
if (([System.Management.Automation.PSTypeName]'DshDwm').Type) {
  $h = [IntPtr][int64]$args[0]
  $v = 1
  $hr = [DshDwm]::DwmSetWindowAttribute($h, 3, [ref]$v, 4)
  Write-Output $hr
} else {
  Write-Output 'AddTypeFailed'
}
`;

module.exports = { getForeground, disableTransitions };
