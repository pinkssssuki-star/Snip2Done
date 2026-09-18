# C#/WPF + WebView2 原型实测

目标：验证「用 C#/WPF 套 WebView2 复用现有 HTML UI」这条路值不值得走 —— 拿体积、内存、启动、截图延迟四项和现在的 Electron 版正面对比。

原型代码在 `legacy/wpf-webview2/`（已归档），**`legacy/electron/src/renderer/` 里的 HTML/CSS/JS 一行都没改**。

---

## 一句话结论

**分发体积赢了 100 倍，但运行时（内存、启动）反而输给 Electron。**

WebView2 省掉的是「安装包里的 Chromium」，省不掉「跑起来之后的 Chromium」。对一个要常驻桌面的小面板来说，内存和启动恰恰是最要命的两个指标。

| 指标 | Electron 44 | WPF + WebView2 | 纯原生 WPF（已实现） |
| --- | --- | --- | --- |
| 分发体积 | 98.5 MB / 146 MB | 0.94 MB | **0.14 MB**（exe 142 KB，零依赖 DLL） |
| 进程数 | 5 | 7 | **1** |
| 总工作集 | 419 MB | 653 MB | **108 MB**（采集进行中 ~131 MB） |
| 总私有内存 | 237 MB | 534 MB | **108 MB** |
| 面板首帧 | 330–410 ms | 2800–3100 ms | **619–651 ms** |
| 截图 BitBlt（2560×1440） | 357–476 ms（desktopCapturer） | 26–38 ms | 28–38 ms |
| 线程数 | —— | —— | **34** |

> 三档跑在完全相同的口径下：同一台机器、同一份 `data.json`、同一套测量脚本、都在「面板首帧之后再等 3 秒」取样。
> 纯原生那一档**功能已经齐了**：五种视图、卡片、衰减、悬停预览、区域框选层、归类窗、详情窗、记录窗、热键、截图。

---

## 1. 体积

```
RemeberEr.Wpf.exe                  39.0 KB   ← 本项目全部代码
Microsoft.Web.WebView2.Core.dll   681.8 KB   ← WebView2 SDK
WebView2Loader.dll                159.8 KB   ← 原生加载器
Microsoft.Web.WebView2.Wpf.dll     84.3 KB
--------------------------------------------------
合计                              964.9 KB   ≈ 0.94 MB / 4 个文件
```

前提前提：目标机要有 WebView2 Runtime（Win11 自带；Win10 基本随 Edge 装上，本机是 153.0.4234.32），.NET Framework 4.8 也是 Win10/11 自带。**所以这 0.94 MB 就是全部。**

对比 Electron 的 `RemeberEr-0.5.0-portable.exe` 98.5 MB / `-win.zip` 146 MB。

---

## 2. 内存（就绪 +3 秒）

WebView2 那 6 个子进程的构成（已用 WMI 按命令行 `--user-data-dir=...wpf-userdata` 逐个核对归属，确认没有把 Edge/小组件的 WebView2 算进来）：

| 角色 | 工作集 | 私有 |
| --- | --- | --- |
| 浏览器主进程 | 177.8 MB | **302.8 MB** |
| GPU 进程 | 149.2 MB | 32.4 MB |
| 渲染进程 | 131.2 MB | 45.9 MB |
| 三个小进程（网络/工具/crashpad） | 63.4 MB | 22.8 MB |
| 本体 `RemeberEr.Wpf.exe` | 132.6 MB | 130.4 MB |
| **合计** | **654.3 MB** | **534.4 MB** |

主要是那个浏览器主进程的 302 MB 私有内存。Electron 同样跑 Chromium，但它的进程模型把开销摊得更平（5 进程 419 MB / 237 MB）。

---

## 3. 启动

```
WebView2 初始化完成 @ ~700 ms   ← 起浏览器进程 + 建环境
NavigationCompleted @ ~2800 ms  ← 大头在这：导航到页面并完成首帧用了 ~2.1 s
```

Electron 冷启动一次是 1478 ms，之后稳定在 330–410 ms。原生 WPF 是 295–442 ms。

---

## 4. 截图延迟

用 GDI `BitBlt` 直接抓屏（C# P/Invoke，不经过任何浏览器 API）：

| 路径 | 主屏 2560×1440 |
| --- | --- |
| C# `BitBlt` | 26 / 33 / 38 ms（首次 57 ms 含预热） |
| 之前 Node + koffi 原生测量 | 29–34 ms |
| Electron `desktopCapturer` | 357–476 ms |
| C# 抓双屏全画幅 5120×2008 | 79–107 ms（按像素线性外推与上面吻合） |

**C# 的抓屏和之前 Node 直调 Win32 的结果一致**，两条原生路都远快于 Electron 的 `desktopCapturer`。这一项架构无关，只要不走 Chromium 的采集 API 就能拿到。

---

## 5. 透明：能用，而且和 Electron 一致

这是动手前最大的未知数 —— WebView2 是子 HWND，WPF 的 `AllowsTransparency=true` 是分层窗口，业界普遍说法是「空域冲突，做不了」。

**实测结论：能。** 决定性证据是 `shots/wpf-probe-red.png`：把 WPF 窗口底色刷成纯红，如果 `DefaultBackgroundColor = Transparent` 没生效，红色应该完全看不见；实际面板的半透明底色被染成了粉红 —— 说明 HTML 的 `rgba()` 底色确实透到了窗口底层，逐像素桌面透明和 Electron 版表现一致。

同时试了 DWM 背景材质那一档（`DwmExtendFrameIntoClientArea(-1)` + `DWMWA_SYSTEMBACKDROP_TYPE=Acrylic`，两个调用都 `hr=0` 成功），但截图和普通不透明档几乎逐字节相同 —— WebView2 铺满整个窗口，把 DWM 背景材质完全盖住了。**这一档对本项目没有意义。**

| 文件 | 说明 |
| --- | --- |
| `shots/wpf-layered.png` | 分层窗口，真正的逐像素透明 |
| `shots/wpf-probe-red.png` | 红色探针，证明透明生效 |
| `shots/wpf-opaque.png` / `shots/wpf-acrylic.png` | 对照档 |

---

## 6. UI 还原度

`panel.html` / `panel.css` / `panel.js` 一行未改，在 WebView2 里完整渲染：Todo 卡片、逾期红标、Soon 标、Wantdo 渐变缩略图、生命条「还能撑 6.8 天」、页脚「本周完成 0 件 · 纸篓里 1 个想法」，以及面板圆角，全部正常。

唯一要额外处理的：`panel.css` 里的 `-webkit-app-region: drag` WebView2 不认，改由注入的 shim 监听标题栏 `mousedown`，转成原生 `ReleaseCapture()` + `WM_NCLBUTTONDOWN/HTCAPTION` 拖窗。

接口对接靠 `legacy/wpf-webview2/shim.js`：把 Electron preload 暴露的 `window.api` 一比一实现在 `chrome.webview.postMessage` 上，前缀注入（`AddScriptToExecuteOnDocumentCreatedAsync`）保证早于 `panel.js` 执行。

---

## 7. 移植忠实度

C# 的 `Store.cs` / `Decay` 是对 `store.js` / `decay.js` 的直译，读写**同一份 JSON 结构**。

为了证明不是「看起来像」，`verify-snapshot.js` 把「现在」钉死成同一个时间戳，让两个实现对同一份 `data.json` 各跑一次 `snapshot()`，然后逐字段 diff（含 `opacity` / `life` / `daysLeft` / `overdue` / `binMs` / `elapsedMs` / `doneHour` / `decayCfg` / `stats`）：

```
JS  items=5 (want/active=2)  settings={"demoFastDecay":false,"panelOpacity":0.8}
CS  items=5 (want/active=2)  settings={"demoFastDecay":false,"panelOpacity":0.8}

✔ 逐字段完全一致（5 个条目 × 全部衰减字段，容差 1e-9）
```

---

## 8. 怎么启动

**WPF+WebView2 和纯原生 WPF 不是两个程序，是同一个 `RemeberEr.Wpf.exe` 的两个 `--mode`。** 默认 `layered` 就是 WebView2 那一档。

### 最省事：双击 `csharp\wpf\` 下的三个启动器

| 文件 | 等于 | 用途 |
| --- | --- | --- |
| `启动-WebView2版.cmd` | `--mode=layered` | 复用 HTML UI 的那一档，逐像素透明 |
| `启动-纯原生版.cmd` | `--mode=native` | 完全不起 WebView2，纯 WPF 假面板，量开销地板用 |
| `启动-基准测试.cmd` | `--mode=layered --bench --primary` | 跑 4 次截图后打印内存/启动/延迟并自动退出 |

三个 `.cmd` 都只是 `run.ps1` 的壳，里面是纯 ASCII，不挑代码页。`out/` 不入库，所以**第一次跑（或 `git clone` 之后）启动器会自动先编译一遍**，不用手动 build。

面板出现在屏幕右下角，热键：`Ctrl+Shift+A` 截图 / `Ctrl+Shift+H` 显示隐藏 / `Ctrl+Shift+Q` 退出。

### 或者命令行

```powershell
# 默认就是 WebView2 + 分层透明
powershell -ExecutionPolicy Bypass -File csharp\wpf\run.ps1

# 纯原生 WPF
powershell -ExecutionPolicy Bypass -File csharp\wpf\run.ps1 -Mode native

# 基准测试（跑完自己退出）
powershell -ExecutionPolicy Bypass -File csharp\wpf\run.ps1 -Bench -Primary

# 换档 / 强制重编
powershell -ExecutionPolicy Bypass -File csharp\wpf\run.ps1 -Mode acrylic -Rebuild
```

### 直接调 exe

```powershell
csharp\wpf\out\RemeberEr.Wpf.exe --root=<仓库根> --mode=layered
csharp\wpf\out\RemeberEr.Wpf.exe --root=<仓库根> --mode=native --bench --primary
```

`--root` 一定要给对（指到仓库根，不是 `out/`），否则找不到 `src/renderer` 和 `shim.js`。

### 编译

只有需要单独编译时才用得上。这台机器**没装 .NET SDK，也没有 .NET Framework 目标包**，所以走的是 VS 2022 自带的 Roslyn `csc.exe` + 系统里的 WPF 程序集直接编译，不需要装任何东西：

```powershell
powershell -ExecutionPolicy Bypass -File csharp\wpf\build.ps1
```

### 前提

- **WebView2 Runtime**：Win11 自带；Win10 基本随 Edge 装上。只有 `native` 档不需要它。
- **.NET Framework 4.8**：Win10/11 自带。

### 数据

读写的是工作区里的副本 `.tmp/wpf-data/`，首次运行会从 `%APPDATA%\remeber-er` 复制一份，**不动 Electron 版的真实数据**。

---

## 9. 踩到的坑（按踩到的顺序）

1. **没有 .NET SDK / 目标包**：改用 VS 自带的 Roslyn `csc.exe` 显式引用 `Framework64\v4.0.30319\WPF\` 下的 WPF 程序集，绕开 MSBuild 和目标包。
2. **无 BOM 的 `.ps1` 被 PowerShell 按 GBK 读**，中文注释和字符串全乱、脚本解析失败。必须写 UTF-8 BOM。
3. **受限沙箱下 WebView2 创建控制器报 `E_UNEXPECTED (0x8000FFFF)`**：Chromium 的命名管道 IPC 被拦。放权后一次跑通，和之前 Electron 遇到的是同一处边界。
4. **`CoreWebView2EnvironmentOptions.CustomSchemeRegistrations` 是只读属性，且用无参构造时为 `null`** —— 只能在构造函数第 5 个参数里传进去。这个坑不会报错，只会静默地让 `appimg://` 全部 404。
5. **进程 DPI 不感知**：`SystemParameters.WorkArea` 返回物理像素（2560×1392），而窗口坐标被系统按 96 DPI 虚拟化，两者混用直接把窗口丢到屏幕外，同时整个 UI 被系统拉伸变糊。必须在 `Main` 第一行调 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`。
6. **双屏时截图坐标要减 `SM_XVIRTUALSCREEN`**：位图的 `(0,0)` 是虚拟屏原点不是屏幕 `(0,0)`，本机原点是 `(-2560,0)`，不减就一直截到隔壁屏。
7. **`-webkit-app-region: drag` 在 WebView2 里无效**，要自己接 `WM_NCLBUTTONDOWN`。
8. **`BitBlt` 要带 `CAPTUREBLT`** 才截得到分层窗口，否则 `layered` 档的面板在截图里直接消失。
9. **DPI 感知光靠 `SetProcessDpiAwarenessContext` 不保险**：那句在原生版里返回 `False`（感知已被别的东西定死），结果混 DPI 双屏下 `GetMonitorInfo` 返回的是**虚拟化尺寸** —— 副屏报 `1707×960` 而不是 `2560×1440`，框选层会盖错范围，整屏截图还会被系统缩放变糊。改成在 `app.manifest` 里写 `PerMonitorV2` 之后，同一块屏正确报成 `2560×1440 @1.5x`。
10. **WPF 窗口的 `Left/Top/Width/Height` 在混 DPI 下是有歧义的**（按哪块屏的缩放算？）。框选层要正好盖住一整块屏，直接用 `SetWindowPos` 按物理像素摆，绕开这套换算。

---

## 10. 纯原生 WPF 版已经做出来了（方案 C）

结论先行：**C 的数字全部兑现，而且是三档里唯一同时赢体积、内存、启动的。**

`native/` 就是它。UI 全部重写成 XAML，**一点 Chromium 都没有**：

```
RemeberEr.Native.exe   87.5 KB   ← 全部代码 + 编译后的 BAML
RemeberEr.Native.pdb  151.5 KB   ← 调试符号，分发时不用带
```

对比一下 `legacy/electron/src/renderer/` 里那 47 KB 的 HTML/CSS/JS —— 重写之后整个程序 87.5 KB，比原来那堆前端资源还小。

### 已经落地的部分

- **五种视图**：主面板（Todo / Wantdo）、Paper Plane Bin、Done（按天分组）、Settings、快速记一笔
- **卡片体系**：Todo 卡片（复选框 / 逾期红标 / Today·Soon 药丸 / 缩略图 / 悬停出 ✕）、Wantdo 图片磁贴（渐变色 + 生命周期条 + 「⏳ 还能撑 X 天」）、纸篓卡片、Done 条目、空态
- **区域框选层**：盖住鼠标所在那块屏，铺整屏截图，拖动出框；四周用 EvenOdd 几何挖洞压暗、选区白边、实时尺寸标签、顶部提示条；**松手后选区还在，可以拖角微调、拖内部平移**，`Enter` / 双击确认；小于 6×6 当误点、`Esc` / 右键取消
- **三个附属窗口**：截图后的归类窗（Today / Soon / Wantdo + 标题，Esc 算了、1/2/3 直接选）、详情窗（自动保存备注、生命值、Get Done）、完成记录窗（写心得 / 回看小结两种模式，含自动小结与统计卡片）
- **交互动画**：已完成的 Todo 再点一下会「消散」（0.3s 内微放大 1.05 + 淡出 + 高度收到 0），和 `panel.css` 的 `bubble-out` 一致
- **纸篓卡片去色**：用 WPF 自带的 `FormatConvertedBitmap` 转 Gray8，对应 CSS 的 `filter: grayscale(0.7)` —— 不加像素着色器，零依赖
- **交互**：点一下 Wantdo = 看一眼 = 续命；勾选 Todo 先不收走、再点一下才进 Done；纸篓点一下捡回来；折叠成 190×44；标题栏拖动；长悬停 550ms 弹出放大预览
- **衰减完全复用**：`Store.cs` / `Decay` 直接 link 自 `legacy/wpf-webview2/src/`，和 `store.js` 逐字段对齐过的那份，一个字没改
- **全局热键**：`Ctrl+Shift+A` 截图 / `Ctrl+Shift+H` 显隐 / `Ctrl+Shift+Q` 退出
- **截图**：GDI `BitBlt` 28–38 ms（Electron 的 `desktopCapturer` 是 357–476 ms）

### 启动时间是怎么从 1138ms 压回 552ms 的

一开始加完三个附属窗口，首帧从 ~500ms 涨到 ~1138ms。加了分段计时之后定位到两件事，都很典型：

```
[t=34ms]  Application 已建
[t=50ms]  Theme.xaml 已加载
[t=827ms] PanelWindow 已构造      ← 775ms 全在构造函数里
[t=1125ms] 窗口句柄就绪
```

再往里细分，发现是 `Refresh()` 里的图片解码：

| 猜测 | 实测 |
| --- | --- |
| PNG 解码占了 UI 线程 | 改成后台单线程低优先级解码 → 1015ms |
| 应该写缩略图，别每次解 7MB 原图 | 加了 `<id>.thumb.png` 缓存（平均 37.8 KB）→ 916ms |
| **还有个同步 `Images.Get()` 漏网** | Want+Bin 从 **553ms → 2ms** → **552ms** |

第三个才是真凶：之前批量改异步时，正则只覆盖了 `Thumb` 后面紧跟 `ImagePath` 的那几处，`MakeWant` 里那条后面还有别的属性，被漏掉了。有意思的是中间做 A/B（`--nothumb` 完全关掉解码）时结果几乎没变，正是这个反直觉的结果逼着继续往里查——如果只看那个 A/B 就会误判成「跟图片无关」。

### 怎么启动

```powershell
# 双击：csharp\native\启动-原生版.cmd
# 或
powershell -ExecutionPolicy Bypass -File csharp\native\run.ps1
powershell -ExecutionPolicy Bypass -File csharp\native\run.ps1 -Bench -Primary   # 基准测试
powershell -ExecutionPolicy Bypass -File csharp\native\run.ps1 -View set        # 直接进某一页
```

### 编译上没有 .NET SDK 也能干

这台机器没有 .NET SDK，也没有 .NET Framework 目标包，但 VS 2022 里有 MSBuild 和 `Microsoft.WinFX.targets`。
缺的只有参考程序集 —— 而官方正好把它作为 NuGet 包发布，所以：

1. `node native/tools/get-refasm.js` 从 nuget.org 取 `microsoft.netframework.referenceassemblies.net48`，解到 `native/refasm/`
2. `native/build.ps1` 用 MSBuild 编译，把 `/p:TargetFrameworkRootPath` 指过去

这样 `MarkupCompilePass` 就能正常跑，XAML 被编成 BAML 嵌进 exe，`x:Class`、`InitializeComponent()`、事件绑定全部可用 —— 是正经的编译期 XAML，不是运行期 `XamlReader` 凑数。

### 还剩什么没搬

功能上**已经对齐了**。剩下这几项是主观选择，不是缺口：

| 项 | 为什么没做 |
| --- | --- |
| 框选后按方向键微调 | 拖角已经够用，加键盘微调是锦上添花 |
| 纸篓去色做到精确的 70% | 现在是全灰，CSS 是 `grayscale(0.7)`；差 30% 肉眼几乎看不出 |
| 「上次用的哪块屏」记忆 | 现在每次取鼠标所在那块屏 —— 这和 Electron 版的 `displayForPoint()` 行为一致，不是缺口 |
| 多截图格式（JPEG 可选） | 存 PNG 是和 JS 版数据格式对齐的硬要求 |

真正没达标的只有一项：**启动时间**。见下。

---

## 11. 建议（更新）

方案 C 已经从「值得做」变成「做完了」，数字如下：

| | 分发 | 内存 | 启动 | 状态 |
| --- | --- | --- | --- | --- |
| **A. Electron（现状）** | 98.5 MB | 419 MB | 350 ms | 功能最全，是当前可发布的版本 |
| **B. WPF + WebView2** | 0.94 MB | 653 MB | 2900 ms | 验证完毕，**不建议走** —— 两项都输给 A |
| **C. 纯原生 WPF/XAML** | **0.14 MB** | **108 MB** | 630 ms | **功能已全对齐**（五种视图 + 框选层 + 归类/详情/记录窗 + 消散动画 + 去色） |

**现在的建议：如果确定要长期常驻，就让 C 取代 A。**

理由：C 的体积和内存分别是 A 的 1/750 和 1/4，启动慢 230 ms —— 这 230 ms 是 WPF 冷启动的固定成本（窗口句柄 + 首帧就占了 270 ms），不是可优化的对象。唯一的代价是那 47 KB 前端要维护成 XAML，而这件事**已经做完了**，沉没成本已经付掉。

B 则可以正式判死：它当初唯一的卖点是「1 MB 分发 + 复用 HTML」，但实测内存 653 MB、启动 2.9 秒，两个关键指标都比 A 差，省下的那 0.85 MB 分发体积换不来这个退化。

关于那个「~350 ms」的目标：**没达到，实测 630 ms。** 那个数字本来是拿**假面板**量出来的地板值，不是可交付面板的真实数字。真实面板（五种视图 + 四个窗口 + 全部卡片模板）的构成是：

```
Application + Theme.xaml    ~50 ms
PanelWindow 的 XAML 解析    ~135 ms
Store + ViewModel + Refresh  ~15 ms
窗口句柄 + 首帧             ~270 ms   ← WPF 自身的固定成本
                           ─────────
                           ~470 ms（干净数据）
                           ~630 ms（22 条真实数据、含缩略图）
```

剩下能挤的只有 XAML 解析那一块（把非主视图拆成按需加载的 ResourceDictionary），乐观估计能省 50–80 ms —— 也就是到 550 ms 上下，**离 350 ms 仍然差得远**。那 270 ms 是窗口句柄 + 首帧，属于 WPF 自身的启动成本，不是能靠重构消掉的。

结论：**350 ms 这个目标对 WPF 不现实，我不打算继续追。** 对一个「开机就在、之后一直挂着」的常驻面板来说，冷启动 630 ms 完全够用；真正重要的内存（108 MB）和体积（0.14 MB）都超额达成了。

剩下的工作按性价比排序：

1. 框选后方向键微调 —— 体验上的小补丁
2. 纸篓去色精确到 70%

顺序仍然是：**先把设计验证完，再决定要不要把 Electron 版整个替换掉。** 技术上，C 已经证明自己能扛 —— 功能全对齐、1 进程、108 MB、0.14 MB。
