using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RemeberEr
{
    /// <summary>
    /// 单实例保护。
    ///
    /// 上游（Windows）用的是命名 mutex + 命名事件：第二个实例发现自己不是唯一的，
    /// 就 Set 一下事件、自己立刻退出；那个事件把正在跑的面板叫到眼前。
    ///
    /// mac 上没有等价的命名内核对象，用手边就有的两样东西实现同样语义：
    ///   · **flock**（文件锁）—— 谁拿住锁谁是唯一实例。选它而不是「pid 文件 + kill(0)」
    ///     是因为进程崩掉时内核会自动释放 flock，不会留下一个假的「已在运行」。
    ///   · **FIFO**（命名管道）—— 第二个实例往里写一个字节就退出，
    ///     正在跑的那个读到了就把面板叫到眼前。等价于上游那个命名事件。
    ///
    /// 判定必须放在**碰日志和数据目录之前** —— 第二个实例不该留下任何痕迹。
    /// 上游的原话是「原来连日志都会被它清掉」。
    /// </summary>
    public static class SingleInstance
    {
        const string LockFile = "single-instance.lock";
        const string WakePipe = "single-instance.pipe";

        // LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8（<sys/file.h>）
        const int LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8;

        [DllImport("libc", SetLastError = true)]
        static extern int flock(int fd, int operation);

        [DllImport("libc", SetLastError = true)]
        static extern int open(string path, int flags, int mode);

        [DllImport("libc", SetLastError = true)]
        static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        static extern int mkfifo(string path, int mode);

        [DllImport("libc", SetLastError = true)]
        static extern IntPtr read(int fd, byte[] buf, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        static extern IntPtr write(int fd, byte[] buf, IntPtr count);

        const int O_RDONLY = 0, O_WRONLY = 1, O_NONBLOCK = 4;

        static int _lockFd = -1;
        static volatile bool _watching;
        static System.Threading.Thread _watchThread;

        /// <summary>数据目录（判定要早于初始化数据目录，所以由调用方传进来）。</summary>
        static string _dir;

        /// <summary>
        /// 试着成为唯一实例。
        /// 返回 false 表示「已经有一个在跑」—— 调用方应该**立刻退出**，
        /// 而且在返回 false 之前已经顺手把那个实例叫醒了。
        /// </summary>
        public static bool TryAcquire(string dataDir)
        {
            _dir = dataDir;
            try
            {
                Directory.CreateDirectory(dataDir);
                string lockPath = Path.Combine(dataDir, LockFile);

                // 先开文件再锁：flock 是锁在 fd 上的，fd 关掉（进程退出）就自动释放
                _lockFd = open(lockPath, O_RDONLY | O_NONBLOCK, 0);
                if (_lockFd < 0)
                {
                    // 文件不存在就建一个空的再开
                    try { File.WriteAllText(lockPath, ""); } catch (Exception) { }
                    _lockFd = open(lockPath, O_RDONLY | O_NONBLOCK, 0);
                }
                if (_lockFd < 0) return true;   // 拿不到锁文件就别挡着人家启动

                if (flock(_lockFd, LOCK_EX | LOCK_NB) != 0)
                {
                    // 已经有人在跑：叫醒它，然后交回 false 让调用方退出
                    WakeExisting();
                    try { close(_lockFd); } catch (Exception) { }
                    _lockFd = -1;
                    return false;
                }

                EnsurePipe();
                StartWatching();
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("  !! 单实例判定失败（当成唯一实例继续跑）：" + ex.Message);
                return true;
            }
        }

        /// <summary>面板被「叫醒」时要做什么：显示、取消贴边隐藏、提到前面。</summary>
        public static event Action Woken;

        static void EnsurePipe()
        {
            try
            {
                string pipe = Path.Combine(_dir, WakePipe);
                if (!File.Exists(pipe))
                {
                    // 0600：只有当前用户能往里写
                    if (mkfifo(pipe, 0x180) != 0 && !File.Exists(pipe))
                        Program.Log("  !! 建唤醒管道失败（errno=" + Marshal.GetLastWin32Error() + "）");
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 后台盯着那个管道。第二个实例往里面写一个字节，这里就读到、叫醒面板。
        ///
        /// 用阻塞读 + 后台线程，而不是轮询 —— 轮询要在没有第二个实例的时候也一直醒着，
        /// 这个工具是常驻的，没必要为了一个几乎不会发生的事件一直耗电。
        /// </summary>
        static void StartWatching()
        {
            if (_watching) return;
            _watching = true;

            _watchThread = new System.Threading.Thread(() =>
            {
                string pipe = Path.Combine(_dir, WakePipe);
                var buf = new byte[8];
                while (_watching)
                {
                    int fd = -1;
                    try
                    {
                        // 每次重新开：读到一个字节之后这次读就结束了，得重新开
                        fd = open(pipe, O_RDONLY, 0);
                        if (fd < 0) { System.Threading.Thread.Sleep(500); continue; }

                        // 阻塞读（不加 O_NONBLOCK）—— 没数据就睡着，不占 CPU
                        var n = read(fd, buf, (IntPtr)buf.Length);
                        if (n.ToInt64() > 0)
                        {
                            Program.Log("  单实例：收到唤醒信号，把面板叫到眼前");
                            var h = Woken;
                            if (h != null) h();
                        }
                    }
                    catch (Exception) { System.Threading.Thread.Sleep(500); }
                    finally { if (fd >= 0) { try { close(fd); } catch (Exception) { } } }
                }
            })
            {
                IsBackground = true,
                Name = "single-instance-watch",
            };
            _watchThread.Start();
        }

        /// <summary>第二个实例：叫醒已经在跑的那个，然后自己就该退出了。</summary>
        static void WakeExisting()
        {
            try
            {
                string pipe = Path.Combine(_dir, WakePipe);
                if (!File.Exists(pipe)) return;
                // O_NONBLOCK：万一没人读，不要在这里卡住（反正我们马上要退出）
                int fd = open(pipe, O_WRONLY | O_NONBLOCK, 0);
                if (fd < 0) return;
                try { write(fd, new byte[] { 1 }, (IntPtr)1); }
                finally { try { close(fd); } catch (Exception) { } }
            }
            catch (Exception) { }
        }

        /// <summary>退出时释放锁（进程死了内核也会放，这里只是干净一点）。</summary>
        public static void Release()
        {
            _watching = false;
            try { if (_lockFd >= 0) { flock(_lockFd, LOCK_UN); close(_lockFd); _lockFd = -1; } }
            catch (Exception) { }
        }
    }
}
