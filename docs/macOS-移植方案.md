# macOS 移植方案（Avalonia）

> 状态：**决策已定，实施中**。本文档是移植期间的唯一依据，任何实现偏离都要回来改这里。
> 目标版本：`native/` 主版本（纯原生 WPF）。`legacy/` 不动。

---

## 1. 为什么要移植、难点在哪

主版本是 **.NET Framework 4.8 + WPF** 的原生 Windows 桌面程序。WPF 是 Windows 独有的 UI 框架，
**在 macOS 上没有任何运行方式**（不是「配置问题」，是框架本身不存在）。

难点不在 UI 语法，而在这三块 Windows 专有能力：

| 能力 | 当前实现 | mac 上的替代 |
|---|---|---|
| 抓屏 | GDI `BitBlt` / `CreateCompatibleBitmap` | ScreenCaptureKit / CoreGraphics |
| 全局热键 | `RegisterHotKey` + `WndProc` 消息循环 | Carbon `RegisterEventHotKey` |
| 取「鼠标下窗口标题」 | `WindowFromPoint` + `EnumWindows` + `GetWindowText` | Accessibility API (`AXUIElement`) |
| 多显示器 / DPI | `EnumDisplayMonitors` / `GetDpiForWindow` / `shcore` | AppKit `NSScreen` / `CGDisplay` |
| 窗口置顶、圆角、投影 | `SetWindowPos` / `dwmapi` | Avalonia 内建 + AppKit |
| 数据目录 | `%APPDATA%\remeber-er` | `~/Library/Application Support/remeber-er` |

好消息：程序**没有第三方依赖**（README 明确「零 Chromium、零第三方依赖」），
所以移植不涉及替换一堆 Windows-only 的 NuGet 包。

---

## 2. 选型决策

选定 **Avalonia 12.1.2**（跨平台 .NET UI 框架）+ 自写 macOS 原生互操作层。

**为什么是 Avalonia 而不是别的：**

| 方案 | 否决理由 |
|---|---|
| WPF + 虚拟机/Wine | 不算移植，且需要用户装一堆东西 |
| MAUI | macOS 支持弱，XAML 方言差异比 Avalonia 大，桌面端不是它的主场 |
| Uno Platform | 重、构建链复杂，对「单 exe 小工具」过重 |
| **Avalonia** ✅ | XAML 方言与 WPF 最接近（大量代码可近乎逐行搬）、原生 arm64、可单文件发布、无运行时依赖 |

**为什么不用 Electron 版救急**：Electron 存档版确实能较快在 mac 上跑，但 README 的实测数据
（98.5 MB / 5 进程 / 419 MB 内存）正是这个项目**主动否掉**的方向，
而项目当初选纯原生的整个理由就是体积和内存。移植目标是保住这个结论，不是绕开它。

**运行时**：.NET 10 LTS（`10.0.12`，SDK `10.0.401`）。不用 11.0（还是 RC）。
Avalonia 12.x 支持 `net10.0`。

---

## 3. 代码复用盘点（实测）

| 文件 | 行数 | WPF/Win32 依赖 | 处置 |
|---|---|---|---|
| `Json.cs` | 302 | **0** | ✅ **逐字复用** |
| `Store.cs` | 507 | **0** | ✅ **逐字复用** |
| `Mvvm.cs` | 142 | `ICommand` / `Dispatcher` | 🔧 换 Avalonia 等价物 |
| `ViewModels.cs` | 908 | `BitmapImage` / `BitmapFrame` / `Dispatcher` | 🔧 缩略图解码换成 Avalonia + CoreGraphics |
| `Interop.cs` | 315 | **34 处 Win32 P/Invoke** | 🔴 重写为 mac 平台层 |
| `Capture.cs` | 134 | 走 `Interop` | 🔧 改走平台层接口 |
| 6 个窗口 `.xaml.cs` | ~2700 | 少量 WPF + `Interop` | 🔴 移植 |
| 6 个 `.xaml` + `Theme.xaml` | ~1900 | XAML 语法 | 🟡 语法适配 |
| `Program.cs` | 184 | 入口 / DPI / 数据目录 | 🔧 重写为 Avalonia 启动 |

**约 809 行纯逻辑零改动**，这是移植成本能被压下来的关键。

---

## 4. 项目结构

原则：**Windows 版必须继续能编译**，共享文件只有一份。

```
native/
  RemeberEr.Native.csproj     ← Windows 版，保持不动
  src/                        ← 共享的纯 C#（两边都引用同一份文件）
    Json.cs                   ← 逐字复用
    Store.cs                  ← 逐字复用
  mac/
    RemeberEr.Mac.csproj      ← 新：Avalonia 可执行项目
    Program.cs                ← Avalonia 启动、参数、数据目录
    Platform/
      IPlatform.cs            ← 平台能力接口（抓屏/热键/窗口探测/显示器）
      MacPlatform.cs          ← macOS 实现（NativeAOT 友好的 P/Invoke）
      MacInterop.cs           ← CoreGraphics / Carbon / Accessibility 声明
    Mvvm.cs                   ← Avalonia 版
    ViewModels.cs             ← Avalonia 版
    Capture.cs                ← Avalonia 版
    Theme.axaml               ← 由 Theme.xaml 适配
    PanelWindow.axaml(.cs)    ← 由 PanelWindow.xaml 适配，逐步迁移
    ...                       ← 其余 5 个窗口
    run.sh / build.sh         ← mac 启动/构建脚本
```

共享文件用 `<Compile Include="../src/Json.cs" Link="..." />` 跨目录引用，
**不复制粘贴** —— 避免两边逻辑漂移（`Json.cs`/`Store.cs` 和 JS 版逐字段对齐过，这份对齐要保住）。

---

## 5. macOS 平台层设计

`Interop.cs` 的角色由 `IPlatform` 接口取代，WPF 版和 Avalonia 版各给一份实现：

```csharp
interface IPlatform {
    IReadOnlyList<DisplayInfo> GetDisplays();
    Screenshot CaptureDisplay(DisplayInfo d);          // 整屏位图
    Screenshot CaptureRect(DisplayInfo d, Rect r);     // 区域位图
    void RegisterHotkey(string id, Modifiers m, Key k, Action cb);
    ForegroundInfo ProbeForegroundWindow();             // 鼠标下窗口的 来源+标题
    void SetTopmost(Window w, bool on);
}
```

### mac 侧 API 映射

| 需求 | API | 权限 |
|---|---|---|
| 抓屏 | `CGDisplayCreateImage` / ScreenCaptureKit | **屏幕录制** |
| 显示器枚举 / 缩放 | `CGGetActiveDisplayList` / `NSScreen` | 无 |
| 全局热键 | Carbon `RegisterEventHotKey` | 无 |
| 鼠标位置 | `NSEvent.mouseLocation` / `CGEventGetLocation` | 无 |
| 鼠标下窗口标题 | `AXUIElementCopyElementAtPosition` → `kAXTitleAttribute` | **辅助功能** |
| 来源应用名 | `NSWorkspace.frontmostApplication` / `AXUIElement` → PID → 应用名 | 辅助功能 |

### 权限（首次运行必须引导用户授权）

macOS 是**按应用签名**授予这两项权限的，不是按脚本：

1. **屏幕录制**（System Settings → Privacy & Security → Screen Recording）
   - 未授权时 `CGDisplayCreateImage` 返回**纯黑/空图**，且不报错 —— 必须显式检测并引导，
     否则用户只会看到「截图是全黑」，无从排查。
2. **辅助功能**（Accessibility）
   - 未授权时 `AXUIElementCopyElementAtPosition` 返回错误。
   - 降级策略：取不到标题就退回到「应用名」，再退回到空 —— 不能因此让截图流程失败。

判定辅助功能是否已授权用 `AXIsProcessTrustedWithOptions`，并可用
`AXIsProcessTrusted` 触发系统弹窗（`kAXTrustedCheckOptionPrompt`）。

> 这两项权限是这个产品在 mac 上的固有成本（「截别人的屏」和「读别人的窗口标题」
> 本质上就是敏感能力）。在文档和启动引导里必须讲清楚用途，不能静默索取。

---

## 6. 数据与迁移

- 数据目录：`~/Library/Application Support/remeber-er`
- **兼容性**：mac 版是全新平台，没有历史数据要迁移；但 JSON 结构与 `%APPDATA%` 版
  **保持逐字段一致**，这样两边的数据文件可以直接互换（便于调试和将来同步）。
- 图片缩略图缓存 `<id>.thumb.png` 的机制保留。

---

## 6.5 上游跟进（v0.9.1）

mac 版目前对应上游 **v0.9.1**（`origin/main` = e000e13）。逐项对照：

| # | v0.9.1 特性 | mac 侧状态 |
|---|---|---|
| 1 | 纸篓/完成按房间分两个标签页 | ✅ 使命 Todo 不能捡回 · 初心 Wantdo 点了捞回 |
| 2 | 倒垃圾（整个程序唯一不可逆的操作，二次确认 4 秒自缩回） | ✅ 只清使命 Todo，初心不动 |
| 3 | 开机自启 | ✅ 改为 macOS LaunchAgents plist |
| 4 | 置顶跳到左上角 / 折叠甩到右下角 | ✅ 折叠改成原地缩放 |
| 5 | 隐藏折叠按钮（折叠着时强制可见） | ✅ |
| 6 | 演示加速重做 | ✅ 含「原来根本不会播」的周期性刷新缺失 |
| 7 | 详情窗/记录窗可多开（同一条去重、依次错开 26px） | ✅ |
| 8 | 补截图 / 手动记一笔 / 两三天随机 48~72 小时 | ✅ 补截图复用同一套框选层 |
| 9 | 设置页不透明底 | ✅ 只有顶栏与透明度那一行故意留透明 |
| 10 | 背景颜色三主题（白/灰/黑） | ✅ 原地改画刷实例 |
| 11 | 三处审美（真色块 / 圆角 / 归类窗排版） | ✅ |
| 12 | 手动记一笔选时间 | ✅ 点 ＋ 弹出时间面板 |
| 13 | 三处再审（按钮描边 / 断点圆角 / 两三天输入框） | ✅ |
| 14 | 时间面板位置 / 去「无期限」/ 磁贴圆角 | ✅ |
| 15 | 纸篓页那句话 | ✅ |
| — | 单实例（同批提交） | ✅ flock + FIFO，判定早于碰日志 |

### 与上游的三处实现差异（都写明了理由）

| 上游 | mac 侧 | 为什么 |
|---|---|---|
| 命名 mutex + 命名事件 | `flock` + FIFO | mac 没有等价的命名内核对象。选 flock 是因为进程崩掉时内核会自动释放，不会留下假的「已在运行」 |
| 改 WPF 的 `SolidColorBrush` 实例颜色 | 同样做法**确实成立** | 实测确认 Avalonia 的 StaticResource 也返回同一实例、且 IBrush 天然可变 —— 所以**没有**改成 DynamicResource（目标里那条假设不成立） |
| 归类窗 `ClipToBounds` + `CornerRadius` | 图片走 Border 底色（ImageBrush） | 两者都不裁剪子元素，用 Image 会盖过圆角 |

### 上游 v0.9.1 自身修掉、mac 侧一并跟进的缺陷

- **演示加速「根本不会播」**：`Sweep()` 挂在 `Snapshot()` 里，而 `Snapshot()` 只有 `Refresh()`
  才跑，程序里没有任何周期性刷新。补上每秒节拍器后实测 20s/40s/65s 依次掉进纸篓。
- **倍率作用方式错误**：旧公式拿总时长乘倍率，一条一小时前放下的 Wantdo 会在点下按钮的
  那一瞬间变成 3.4 年年龄、第一次扫描就被清空。改成只加速「演示开始之后」的时长。

---

## 7. 分阶段实施与验收

每阶段都必须**能编译 + 能运行**，不做「一次性大改后再调」。

### 验证现状（本机实测，分项）

| 能力 | 状态 | 证据 |
|---|---|---|
| 抓屏 | ✔ **真实屏幕验证通过** | `--flow=live`：抓屏 2560x1440 / 106ms，图肉眼核对是真实桌面 |
| 框选层 → 归类 → 落盘 | ✔ 真实屏幕跑通 | `--flow=live` 落盘 room/bucket/图片路径全对，图片 427881 字节 |
| 面板 / 详情 / 记录 / 预览 / 归类窗 | ✔ 6 个窗口渲染核对 | `--xaml-selftest` 6/6 + `--shot-*` 出图 |
| 贴边隐藏 | ✔ 四边几何正确 | `--dock-test`：四边藏位正确且抓手都在屏内 |
| 全局热键 | ✔ 注册与派发验证 | `--hotkey-test`：3 个热键注册成功、派发成功 |
| Wantdo 衰减 | ✔ 数据与文案 | `--vm-selftest`：透明度/生命/淡出提示正确 |
| 纸篓捞回 | ✔ | 面板纸篓视图 + `RestoreCmd`；`--vm-selftest` 覆盖分组 |
| **真人按 ⌘⇧A 出框选层** | ⬜ 待人工按一次 | 热键已注册、框选层及之后全链路已跑通，失败面已被压到很小 |
| v0.9.1 的 15 项特性 | ✔ 全部实现并逐项自检 | 详见 §6.5 的对照表 |
| 单实例 | ✔ | 连点 3 次全部立刻退出、第 1 个仍在运行、日志没被清 |
| 读「鼠标下窗口标题」 | ⬜ 缺「辅助功能」权限 | 已实现降级链：取不到就退回应用名 → 空，**不影响截图流程**（实测确认） |
| 内存实测 | ⬜ 沙箱禁 `ps` | 命令见 §7.6，需在正常终端量 |

**分阶段授权的意外好处**：屏幕录制先授权、辅助功能还没授权，
正好实测出了降级链是有效的 —— 窗口探测返回空，而整个采集流程照常完成、
不抛异常、不卡住。这条路径如果是「全都有权限」的状态，反而永远测不到。

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **P0** | 项目骨架、平台接口、数据层接入 | ✔ 工具链冒烟通过（.NET 10.0.401 + Avalonia 12.1.2 编译并加载成功） |
| **P1** | 空壳窗口能起来 | ✔ 面板显示在右下角，透明/无边框/置顶/中文渲染经真人确认；启动 ~390ms |
| **P2** | 数据流打通 | ✔ `--dumpsnap` 输出与 Windows 版 JSON 逐字段一致；真实显示器枚举与 `system_profiler` 一致；`ViewModels.cs` 移植后 `--vm-selftest` 分组/排序/文案/null 回落全部正确 |
| **P3** | 截图流程 | ✔ **已用真实屏幕端到端验证通过**（抓屏 → 框选 → 归类 → 落盘 → 刷新） |
| **P4** | 面板交互 | ✔ 面板四视图 + 缩略图缓存 + 详情窗 + 完成记录窗 + 悬停预览窗全部完成；**6 个窗口全覆盖** |
| **P5** | 衰减与预览 | ✔ Wantdo 衰减（数据/文案/生命条）+ `Decay` 现算 + 预览窗已完成；**待补**：长悬停触发、贴边隐藏 |
| **P6** | 打磨与打包 | 🔄 `.app` 打包已实现并**实测可运行**（自包含 112 MB、签名身份 com.remeberer.mac、包内抓屏正常）；**待补**：内存实测、图标 |

### 端到端验证（不需要权限）

`--flow=chain` 是这一阶段最重要的验收手段：它自己造一张合成位图喂给
**同一条**采集链路，于是「框选层 → 归类窗 → 落盘 → 面板刷新」被真实驱动一遍，
只有「从屏幕抓图」那一步被替换 —— 而那是整条链路里唯一需要屏幕录制权限的步骤。

```sh
./run.sh --data=/tmp/chain --flow=chain --autoregion=900x560 --autochoose=soon
```

它一上来就抓出五个真实缺陷（详见那条 commit），其中包括两个光看渲染图
或读代码都不容易发现的：macOS 会把无边框窗口夹进「工作区」导致坐标偏移，
以及 `Store.Update` 只认 title/note/reflection、传 `image` 会被静默忽略
（后果是截图存了盘但条目显示「没有截图」）。

合成图刻意画了红块（左上）、蓝块（右上）、白线（正中）做参考物：
裁出来的选区图可以直接肉眼核对坐标，纯渐变是看不出偏移的。

### 已落地的实现清单

```
native/mac/
  RemeberEr.Mac.csproj        Avalonia 项目，链接共享的 ../src/Json.cs / Store.cs
  Program.cs                  入口；参数与 Windows 版一致；数据目录改为 mac 惯例
  Mvvm.cs                     ICommand/转换器/字号与样式类转换
  ViewModels.cs               由 WPF 版移植（908 行，只改缩略图那一小块）
  BitmapUtil.cs               BGRA 缓冲 → Avalonia WriteableBitmap
  Theme.axaml                 设计 token（颜色/转换器/字体），照抄 WPF 版 Theme.xaml
  Styles.axaml                控件样式（Avalonia 不允许样式进 ResourceDictionary）
  PanelWindow.axaml(.cs)      面板：顶栏 + Todo 卡片 + Wantdo 磁贴 + 纸篓/完成/设置
  OverlayWindow.axaml(.cs)    框选层：压暗、拖角微调、拖内部平移、外侧 ✓、快速模式
  Platform/
    IPlatform.cs              平台能力接口（取代 Interop.cs 的 34 处 Win32）
    MacInterop.cs             CoreGraphics / ImageIO / Accessibility / AppKit 声明
    MacPlatform.cs            显示器、鼠标、抓屏、权限、热键、窗口探测
    MacHotkeys.cs             Carbon RegisterEventHotKey 全局热键
    StubPlatform.cs           非 mac 系统上的安全回退
  app.manifest                空清单（mac 不需要 DPI 声明）
  build.sh / run.sh           对应 Windows 的 build.ps1 / run.ps1
```

### 免权限自检（重要）

GUI 程序出错常常只表现为「窗口一闪没了」，日志里什么都没有。为此做了一组
**不需要系统权限**就能跑的自检，把问题分层隔离：

| 命令 | 验证什么 | 需要权限 |
|---|---|---|
| `./run.sh --xaml-selftest` | 各窗口 XAML、控件绑定、主题资源加载 | 无 |
| `./run.sh --dumpsnap=out.json` | 数据层读取与序列化 | 无 |
| `./run.sh --seed` | 往 `--data=` 指定目录灌演示数据 | 无 |
| `./run.sh --vm-selftest` | JSON → ViewModel 的分组/排序/文案/空值回落 | 无 |
| `./run.sh --flow=chain` | **整条采集链路**（框选→归类→落盘→刷新），喂合成位图 | 无 |
| `./run.sh --dock-test` | 贴边隐藏的四边几何与「抓手是否在屏内」 | 无 |
| `./run.sh --shot-*=<png>` | 各窗口渲染成 PNG（面板/归类窗/详情窗/记录窗） | 无 |
| `./run.sh --grab=out.png` | 抓屏是否拿到正确内容（含全黑检测） | 屏幕录制 |
| `./run.sh --overlay-test=out.png` | 框选层渲染、坐标换算、选区几何 | 屏幕录制 |
| `./run.sh --request-perms` | 申请屏幕录制权限（授权后须重启进程） | — |

前四条在**没授权之前**就应全部通过；失败说明是代码问题而不是权限问题。
这一分层是有必要的：`--xaml-selftest` 曾抓出一个真实缺陷
（自检用的应用对象绕开了主题加载，导致测的不是真实启动路径）。

### 移植中踩到的 Avalonia 12 差异（备忘）

| 现象 | 正确写法 |
|---|---|
| `ToolTip="…"` 解析失败 | `ToolTip.Tip="…"` |
| `TextBox.Watermark` 已过时 | `PlaceholderText` |
| `<Style>` 放进 `ResourceDictionary` 报 AVLN3000 | 样式必须放在 `<Styles>` 根下（本项目用独立 Styles.axaml） |
| `new ResourceInclude(uri)` / `new StyleInclude(uri)` 抛 “Source must be set.” | `StyleInclude` 的 `ctor(Uri)` 参数其实是 **baseUri**，`Source` 是 init 属性；直接用 `AvaloniaXamlLoader.Load(uri)` |
| `Rect.Empty` 不存在 | `default(Rect)` |
| `Bitmap.Save(stream)` 已过时 | `Bitmap.Save(stream, new PngBitmapEncoderOptions())` |
| `Avalonia.Controls.Shapes` 需显式 using | `Path` / `Rectangle` 都在这里 |
| `Application.Initialize` 只能跑一次 | 主题加载抽成静态方法 + 幂等开关，供自检复用 |
| XML 注释里不能出现 `--` | 分隔线用 `····`，否则 `AVLN1001`（`<!-- ---- -->` 会炸） |

---

## 7.5 与原版的有意差异

移植不是逐字照抄，下面这些是**看清了原版做法之后有意改的**，每条都写明了理由：

| 项 | 原版 | mac 版 | 为什么 |
|---|---|---|---|
| 抓屏 API | GDI `BitBlt` | `CGDisplayCreateImage` + 自建 CGBitmapContext | 平台对应物；顺带做「全黑检测」把「没权限」和「真是黑图」分开 |
| 窗口阴影 | WPF `DropShadowEffect` | Avalonia `Border.BoxShadow` | 少一层特效，也避开「分层窗口在副屏销毁分段消失」那个老问题 |
| Win32 调用 | 34 处 P/Invoke 散在各窗口 | 收成 `IPlatform` 一个接口 | 窗口代码里不再出现平台专有调用，6 个窗口的移植量因此小很多 |
| 不进 Alt+Tab | `HideFromAltTab` | `Topmost` + 不抢焦点 | 不去硬碰 AppKit 的窗口层级，少一段平台相关代码就少一处风险 |
| 详情窗算尺寸 | `BitmapImage.PixelWidth`（会整张解码） | 只读文件头拿 `PixelSize` | 同一个信息，代价小得多 |
| 预览窗纵向钳制 | `SystemParameters.WorkArea`（主屏） | 光标所在屏的工作区 | 多屏下主屏的工作区是错的 |
| 截图窗尺寸 | 每块屏一层，各铺自己那块屏 | 一条窗口跨虚拟屏 | 换来**支持跨屏框选**（原版做不到）；代价是副屏内容会被等比拉伸 |
| 通知/弹窗 | 无 | 无 | 设计原则第 3 条「不弹窗」，移植时没有引入任何新的打断式 UI |

## 7.6 打包与基准

```sh
native/mac/build-app.sh                 # -> dist/记忆面板.app（29 MB，依赖系统 .NET 运行时）
native/mac/build-app.sh --self-contained # 把 .NET 运行时一起打进去，换机器也能跑
```

**为什么必须打成 `.app`**（而不是发裸可执行文件）：

1. 系统权限（屏幕录制 / 辅助功能）是按 **bundle 身份**授予并记录的。
   跑裸可执行文件时授权会落到「终端」上：换个终端就要重新授权，
   而且系统设置里显示的名字是「终端」，用户认不出那是谁。
2. `LSUIElement=true` 让这个常驻工具不出现在 Dock 里，裸可执行文件做不到。

`Info.plist` 里这两条 **必须** 有，缺了系统会直接拒绝、连弹窗都不给：

- `NSScreenCaptureUsageDescription` —— 抓屏
- `NSAppleEventsUsageDescription` —— 读「鼠标下窗口标题」

打包脚本里两件容易被忽略的事：

- **版本号一致性校验**：`Info.plist` 的 `CFBundleVersion` 必须等于 `Program.cs` 里的
  `Version` 常量，不同就直接拒绝打包（原版 `pack.ps1` 也是从代码读版本，避免两处各写一份）。
- **重新 ad-hoc 签名整个 bundle**：.NET 的 apphost 自带签名，但 `Identifier=apphost`
  且只覆盖那一个可执行文件，会让 TCC 记录的身份与 `CFBundleIdentifier` 对不上。
  重签后 `Identifier=com.remeberer.mac`。

### 体积与基准（本机实测）

| 指标 | Electron 版 | WPF 原生版 | **mac 版** |
|---|---|---|---|
| 分发体积 | 98.5 MB | 0.14 MB | **112 MB**（自包含 .NET 运行时） |
| 进程数 | 5 | 1 | **1** |
| 面板首帧 | 330–410 ms | ~630 ms | **583 ms** |

关于体积要说实话：mac 版 112 MB **比 Electron 版的 98.5 MB 还大**。
原因是平台决定的：Windows 版能做到 0.14 MB，是因为 .NET Framework 4.8 系统自带；
macOS 没有内建 .NET，只能把运行时打进包（框架依赖的 29 MB 版本实测**根本启动不了**，
apphost 只去标准位置找运行时）。

所以选 Avalonia 在 mac 上省下来的**不是分发体积**，而是：
  · 进程数 1（Electron 5）
  · 首帧 583 ms（Electron 330–410 ms，但那是 Chromium 冷启后的空闲态）
  · 内存（待实测）
这一条要在文档里讲清楚，不能拿 Windows 版的 0.14 MB 去暗示 mac 版也一样。

内存需要你在**正常终端**里量（本项目的沙箱禁止 `ps`，报 Operation not permitted）：

```sh
cd native/mac && ./run.sh &
ps -o pid,rss,comm -p $(pgrep -f RemeberEr.Mac | head -1)
```

启动分段可以直接从日志读（每次启动都会重开一份）：

```sh
grep -E "已构造|已显示" ~/Library/"Application Support"/remeber-er/native.log
```

## 8. 已知风险

| 风险 | 说明 | 对策 |
|---|---|---|
| 抓屏在 macOS 14+ 的 API 废弃 | `CGDisplayCreateImage` 已标记废弃，ScreenCaptureKit 是官方方向 | 先跑通 CG，若在目标系统上失效再补 SCK 后端（接口已隔离，替换成本可控） |
| Avalonia XAML 方言差异 | WPF 的部分特性（自定义控件模板、特效）在 Avalonia 需改写 | 逐个窗口迁移，保留 `Theme.axaml` 的 token 设计 |
| 分层窗口/透明行为不同 | WPF 的 `AllowsTransparency` + 投影，Avalonia 机制不同 | 面板透明与圆角在 P1/P4 单独验证 |
| 全屏框选层与其它 App 的 z 序 | mac 上「全屏覆盖所有窗口」受 Spaces / 全屏应用限制 | P3 实测，必要时用 `NSWindow.Level` 提升 |
| 混 DPI 多屏 | 原项目为此踩过坑（`SetWindowPos` 挪窗口导致 DPI 不同步） | 沿用「窗口出生前按目标屏 DPI 算逻辑坐标」的结论 |
| 权限弹窗无法用脚本验证 | 授权需要真人点系统弹窗 | 每阶段留出手动验证清单，交付时说明 |

---

## 9. 开发环境（本机实测）

```
.NET SDK   10.0.401  →  ~/.dotnet        （用户级安装，不需要 sudo）
Avalonia   12.1.2
Node       24.21.0                        （仅 tools/ 下的打包辅助脚本需要）
Xcode CLT  /Library/Developer/CommandLineTools
网络        受限；CLI 需走本地代理 127.0.0.1:7897（已写入 ~/.zshrc 与 NuGet.config）
```

**网络注意事项**：`POST https://github.com/login/oauth/access_token` 直连被阻断，
`gh` / `git` / `dotnet restore` 都必须走代理，否则表现为「超时」而不是「拒绝」。
