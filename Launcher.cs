// -----------------------------------------------------------------------------
//  PCL-跳跳鱼小队专用版 —— 最小单文件 Minecraft 启动器
//  功能级逆向自 Plain Craft Launcher 2 v2.13.1.1 (build 408)
//  闭环：下载游戏 / 安装 Java / 启动游戏
//
//  证据来源见 _re_tools\REVERSE_ENGINEERING.md，每一处非平凡实现均标注了
//  对应的 PCL 源文件与行号，便于复核。
//
//  目标框架 net48（Windows 10/11 自带），编译产物为单个 exe，无需安装运行时。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCLMini
{
    // =========================================================================
    //  版本信息 —— 所有对外呈现的产品字符串集中在这里
    // =========================================================================
    static class Brand
    {
        public const string Name = "PCL-跳跳鱼小队专用版";
        public const string ShortName = "跳跳鱼小队";
        public const string Version = "1.0.0";
        public const string Title = Name + " v" + Version;
        public const string SettingsFile = "PCL-跳跳鱼小队专用版.ini";
        public const string LogFile = "PCL-跳跳鱼小队专用版.log";
        public const string UserAgent = "PCL-Mini/" + Version + " (JumpingFishSquad)";
    }

    // =========================================================================
    //  JSON —— 极简解析器（仅需读取，无第三方依赖）
    //  节点类型：Dictionary<string,object> / List<object> / string / double / bool / null
    // =========================================================================
    static class J
    {
        public static object Parse(string text)
        {
            if (text == null) return null;
            if (text.Length > 3 && text[0] == '﻿') text = text.Substring(1);
            int i = 0;
            SkipWs(text, ref i);
            object v = ParseValue(text, ref i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new Exception("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        static void Expect(string s, ref int i, string token)
        {
            if (i + token.Length > s.Length || string.CompareOrdinal(s, i, token, 0, token.Length) != 0)
                throw new Exception("JSON 语法错误，位置 " + i);
            i += token.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new Exception("JSON 对象缺少键，位置 " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new Exception("JSON 对象缺少冒号，位置 " + i);
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("JSON 对象未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new Exception("JSON 对象语法错误，位置 " + i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("JSON 数组未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new Exception("JSON 数组语法错误，位置 " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            i++; // 起始引号
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new Exception("JSON Unicode 转义不完整");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new Exception("JSON 字符串未闭合");
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            string t = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new Exception("JSON 数字无效：" + t);
            return d;
        }

        // ---------------- 访问辅助 ----------------

        /// <summary>按路径取值，任一层缺失返回 null。</summary>
        public static object Get(object node, params string[] path)
        {
            object cur = node;
            foreach (var key in path)
            {
                var d = cur as Dictionary<string, object>;
                if (d == null) return null;
                if (!d.TryGetValue(key, out cur)) return null;
            }
            return cur;
        }

        public static Dictionary<string, object> Obj(object node) { return node as Dictionary<string, object>; }
        public static List<object> Arr(object node) { return node as List<object>; }

        public static string Str(object node, string def = null)
        {
            if (node == null) return def;
            if (node is string) return (string)node;
            return def;
        }

        public static long Long(object node, long def = 0)
        {
            if (node is double) return (long)Math.Round((double)node);
            if (node is string) { long v; if (long.TryParse((string)node, out v)) return v; }
            return def;
        }

        public static int Int(object node, int def = 0)
        {
            return (int)Long(node, def);
        }

        public static bool Bool(object node, bool def = false)
        {
            if (node is bool) return (bool)node;
            return def;
        }

        /// <summary>按路径取字符串。</summary>
        public static string S(object node, params string[] path) { return Str(Get(node, path)); }
        /// <summary>按路径取对象。</summary>
        public static Dictionary<string, object> O(object node, params string[] path) { return Obj(Get(node, path)); }
        /// <summary>按路径取数组。</summary>
        public static List<object> A(object node, params string[] path) { return Arr(Get(node, path)); }
        /// <summary>按路径取整数。</summary>
        public static long N(object node, long def, params string[] path) { return Long(Get(node, path), def); }
    }

    // =========================================================================
    //  网络 —— 官方 / BMCLAPI 双源，失败自动切换
    //  对应 PCL：ModDownload.DlSourceOrder / DlSourceLauncherOrMetaGet / DlSourceAssetsGet
    //            ModDownload.cs:1562 / 1580-1610 ；ModNet.cs:1047-1072（源淘汰）
    // =========================================================================
    sealed class DownloadTask
    {
        public string Url;          // 官方地址
        public string MirrorUrl;    // 镜像地址（可为 null）
        public string Path;         // 本地落盘路径
        public long Size;           // 期望大小（-1 表示未知）
        public string Sha1;         // 期望 SHA-1（null 表示不校验）
        public string Label;        // 显示名
    }

    static class Net
    {
        public const string UserAgent = Brand.UserAgent;

        /// <summary>true 时优先使用 BMCLAPI；false 时优先官方，失败再回退镜像。</summary>
        public static bool PreferMirror = true;

        /// <summary>是否校验 SHA-1（默认校验新下载文件）。</summary>
        public static bool VerifySha1 = true;

        public static int Threads = 8;
        public static int TimeoutMs = 45000;

        /// <summary>
        /// 生成镜像地址。对应 PCL 的镜像改写规则：
        ///   piston-data / piston-meta / launchermeta / launcher.mojang.com → bmclapi2.bangbang93.com
        ///   resources.download.minecraft.net → bmclapi2.bangbang93.com/assets
        ///   libraries.minecraft.net / maven.minecraftforge.net → bmclapi2.bangbang93.com/maven
        /// </summary>
        public static string Mirror(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string[] metaHosts =
            {
                "https://piston-data.mojang.com/",
                "https://piston-meta.mojang.com/",
                "https://launchermeta.mojang.com/",
                "https://launcher.mojang.com/",
            };
            foreach (var h in metaHosts)
            {
                if (url.StartsWith(h, StringComparison.OrdinalIgnoreCase))
                    return "https://bmclapi2.bangbang93.com/" + url.Substring(h.Length);
            }
            const string res = "https://resources.download.minecraft.net/";
            if (url.StartsWith(res, StringComparison.OrdinalIgnoreCase))
                return "https://bmclapi2.bangbang93.com/assets/" + url.Substring(res.Length);

            string[] mavenHosts =
            {
                "https://libraries.minecraft.net/",
                "https://maven.minecraftforge.net/",
                "https://maven.neoforged.net/releases/",
                "https://maven.fabricmc.net/",
                "https://repo.spongepowered.org/repository/maven-public/",
            };
            foreach (var h in mavenHosts)
            {
                if (url.StartsWith(h, StringComparison.OrdinalIgnoreCase))
                    return "https://bmclapi2.bangbang93.com/maven/" + url.Substring(h.Length);
            }
            return null;
        }

        /// <summary>把 mojang 相关的 URL 改成镜像（布尔开关版）。</summary>
        public static string ToMirror(string url) { return Mirror(url) ?? url; }

        /// <summary>文本请求，带重试与镜像回退。</summary>
        public static string GetString(string url, int timeoutMs = 0)
        {
            var candidates = new List<string>();
            string m = Mirror(url);
            if (PreferMirror && m != null) candidates.Add(m);
            candidates.Add(url);
            if (!PreferMirror && m != null) candidates.Add(m);

            Exception last = null;
            foreach (var u in candidates)
            {
                try
                {
                    return Fetch(u, timeoutMs <= 0 ? TimeoutMs : timeoutMs);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new Exception("无法获取 " + url + "：" + (last == null ? "未知错误" : last.Message), last);
        }

        static string Fetch(string url, int timeoutMs)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = UserAgent;
                    req.Timeout = timeoutMs;
                    req.ReadWriteTimeout = timeoutMs;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    req.AllowAutoRedirect = true;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2) Thread.Sleep(400 * (attempt + 1));
                }
            }
            throw last;
        }

        /// <summary>
        /// 批量下载。已存在且校验通过的文件直接跳过（对应 PCL 的 FileChecker）。
        /// 单个文件失败会在所有源之间轮换，最多重试 3 轮。
        /// </summary>
        public static void DownloadAll(List<DownloadTask> tasks, string title, bool verifyExisting = false)
        {
            if (tasks == null || tasks.Count == 0) { Log.Info("下载 " + title + "：无需下载"); return; }

            int total = tasks.Count;
            int done = 0;
            int failed = 0;
            long bytesDone = 0;
            long bytesTotal = 0;
            foreach (var t in tasks) if (t.Size > 0) bytesTotal += t.Size;

            var progress = new ProgressBar(title);
            progress.Start(total, bytesTotal);

            var queue = new LinkedList<DownloadTask>(tasks);
            var pending = new List<DownloadTask>();
            foreach (var t in tasks) pending.Add(t);
            object gate = new object();

            Exception firstError = null;

            int workerCount = Math.Max(1, Math.Min(Threads, pending.Count));
            var threads = new List<Thread>();
            for (int w = 0; w < workerCount; w++)
            {
                var th = new Thread(() =>
                {
                    while (true)
                    {
                        DownloadTask task;
                        lock (gate)
                        {
                            if (pending.Count == 0) return;
                            task = pending[0];
                            pending.RemoveAt(0);
                        }
                        try
                        {
                            long got = EnsureFile(task, progress, verifyExisting);
                            lock (gate)
                            {
                                done++;
                                bytesDone += got;
                                progress.Report(done, total, bytesDone, bytesTotal);
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (gate)
                            {
                                failed++;
                                if (firstError == null) firstError = ex;
                                progress.Report(done + failed, total, bytesDone, bytesTotal);
                            }
                        }
                    }
                });
                th.IsBackground = true;
                th.Start();
                threads.Add(th);
            }
            foreach (var th in threads) th.Join();
            progress.Finish(done, total, bytesDone, bytesTotal);

            if (failed > 0)
            {
                Log.Error("下载 " + title + " 失败：" + failed + " 个文件未完成");
                if (firstError != null) Log.Error("首个错误：" + firstError.Message);
                throw new Exception("下载 " + title + " 未完成（" + failed + "/" + total + " 个文件失败）");
            }
            Log.Info("下载 " + title + " 完成：" + done + " 个文件");
        }

        /// <summary>确保单个文件可用；返回本次实际下载的字节数（已存在则为 0）。</summary>
        static long EnsureFile(DownloadTask t, ProgressBar progress, bool verifyExisting)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(t.Path));

            // 已存在：按大小（默认）或 SHA-1（--verify）判断是否可用
            // 对应 PCL FileChecker.Check：实际大小/哈希与期望一致即复用
            if (File.Exists(t.Path))
            {
                var fi = new FileInfo(t.Path);
                bool ok = t.Size < 0 ? fi.Length > 0 : fi.Length == t.Size;
                if (ok && verifyExisting && !string.IsNullOrEmpty(t.Sha1))
                    ok = Sha1Of(t.Path).Equals(t.Sha1, StringComparison.OrdinalIgnoreCase);
                if (ok) return 0;
            }

            var urls = new List<string>();
            if (PreferMirror && t.MirrorUrl != null) urls.Add(t.MirrorUrl);
            urls.Add(t.Url);
            if (!PreferMirror && t.MirrorUrl != null) urls.Add(t.MirrorUrl);
            urls = urls.Distinct().ToList();

            string tmp = t.Path + ".pclmini-tmp";
            Exception last = null;
            foreach (var url in urls)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        DownloadOne(url, tmp, progress);
                        // 校验：先大小后 SHA-1（与 PCL FileChecker 顺序一致）
                        var fi = new FileInfo(tmp);
                        if (t.Size >= 0 && fi.Length != t.Size)
                            throw new Exception("大小不符（期望 " + t.Size + "，实际 " + fi.Length + "）");
                        if (VerifySha1 && !string.IsNullOrEmpty(t.Sha1))
                        {
                            string got = Sha1Of(tmp);
                            if (!got.Equals(t.Sha1, StringComparison.OrdinalIgnoreCase))
                                throw new Exception("SHA-1 不符（期望 " + t.Sha1 + "，实际 " + got + "）");
                        }
                        if (File.Exists(t.Path)) File.Delete(t.Path);
                        File.Move(tmp, t.Path);
                        return fi.Length;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        if (attempt < 2) Thread.Sleep(300 * (attempt + 1));
                    }
                }
            }
            throw new Exception("文件下载失败 " + t.Path + "：" + (last == null ? "未知错误" : last.Message), last);
        }

        static void DownloadOne(string url, string tmpPath, ProgressBar progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                    progress.AddBytes(read);
                }
            }
        }

        public static string Sha1Of(string path)
        {
            using (var sha = SHA1.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    // =========================================================================
    //  进度条 —— 单行刷新，输出被重定向时退化为按比例打点
    // =========================================================================
    sealed class ProgressBar
    {
        readonly string _title;
        readonly object _gate = new object();
        long _bytes;
        long _lastPrint;
        readonly bool _interactive;

        public ProgressBar(string title)
        {
            _title = title;
            try { _interactive = !Console.IsOutputRedirected; }
            catch { _interactive = true; }
        }

        public void Start(int total, long bytes)
        {
            if (!_interactive) Log.Info("开始下载 " + _title + "（" + total + " 个文件）");
        }

        public void AddBytes(int n)
        {
            Interlocked.Add(ref _bytes, n);
            if (!_interactive) return;
            long now = Environment.TickCount;
            long last = Interlocked.Read(ref _lastPrint);
            if (now - last < 120) return;
            if (Interlocked.CompareExchange(ref _lastPrint, now, last) != last) return;
            lock (_gate) Render(-1, -1, 0, 0, true);
        }

        public void Report(int done, int total, long bytesDone, long bytesTotal)
        {
            if (!_interactive) return;
            lock (_gate) Render(done, total, bytesDone, bytesTotal, false);
        }

        void Render(int done, int total, long bytesDone, long bytesTotal, bool bytesOnly)
        {
            try
            {
                double frac;
                if (!bytesOnly && total > 0 && done >= 0)
                    frac = (double)done / total;
                else if (bytesTotal > 0)
                    frac = (double)Interlocked.Read(ref _bytes) / bytesTotal;
                else
                    return;
                if (frac > 1) frac = 1;
                int width = 28;
                int fill = (int)(frac * width);
                var bar = new string('#', fill) + new string('-', width - fill);
                Console.Write("\r  {0} [{1}] {2,5:0.0}%", _title, bar, frac * 100);
            }
            catch { }
        }

        public void Finish(int done, int total, long bytesDone, long bytesTotal)
        {
            if (_interactive)
            {
                try
                {
                    Console.Write("\r  {0} [{1}] 100.0%   \n", _title, new string('#', 28));
                }
                catch { }
            }
        }
    }

    // =========================================================================
    //  日志
    // =========================================================================
    static class Log
    {
        public static bool Verbose;
        static readonly object Gate = new object();
        public static string FilePath;

        public static void Info(string msg) { Write("  " + msg, ConsoleColor.Gray); }
        public static void Step(string msg) { Write("» " + msg, ConsoleColor.Cyan); }
        public static void Ok(string msg) { Write("√ " + msg, ConsoleColor.Green); }
        public static void Warn(string msg) { Write("! " + msg, ConsoleColor.Yellow); }
        public static void Error(string msg) { Write("× " + msg, ConsoleColor.Red); }

        public static void Debug(string msg)
        {
            if (Verbose) Write("  " + msg, ConsoleColor.DarkGray);
        }

        static void Write(string msg, ConsoleColor color)
        {
            lock (Gate)
            {
                ConsoleColor old = Console.ForegroundColor;
                try { Console.ForegroundColor = color; } catch { }
                Console.WriteLine(msg);
                try { Console.ForegroundColor = old; } catch { }
                AppendToFile(msg);
            }
        }

        static void AppendToFile(string msg)
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            try
            {
                File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    // =========================================================================
    //  路径与小工具
    // =========================================================================
    static class Util
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);

        /// <summary>
        /// 短路径化（8.3）。PCL 用它避免含空格/非 ASCII 的路径进入命令行
        /// （PathUtils.ToShortPath，ModLaunch.cs:2226-2244 各处调用）。
        /// </summary>
        public static string ToShortPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return path;
                var sb = new StringBuilder(1024);
                int n = GetShortPathName(path, sb, sb.Capacity);
                if (n > 0 && n < sb.Capacity) return sb.ToString();
            }
            catch { }
            return path;
        }

        public static string Slash(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            return p.EndsWith("\\", StringComparison.Ordinal) ? p : p + "\\";
        }

        public static string TrimSlash(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            return p.TrimEnd('\\');
        }

        public static bool IsAsciiOnly(string s)
        {
            if (s == null) return true;
            foreach (char c in s) if (c > 127) return false;
            return true;
        }

        /// <summary>当前系统 ANSI 代码页是否为 GBK（PCL 的 ModBase.IsGBKEncoding）。</summary>
        public static bool IsGbkEnvironment
        {
            get
            {
                try { return CultureInfo.CurrentCulture.TextInfo.ANSICodePage == 936; }
                catch { return false; }
            }
        }

        public static void EnsureDir(string dir)
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>深拷贝 JSON 节点（对应 Newtonsoft 的 DeepClone）。</summary>
        public static object Clone(object node)
        {
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                var r = new Dictionary<string, object>(d.Count, StringComparer.Ordinal);
                foreach (var kv in d) r[kv.Key] = Clone(kv.Value);
                return r;
            }
            var l = node as List<object>;
            if (l != null)
            {
                var r = new List<object>(l.Count);
                foreach (var item in l) r.Add(Clone(item));
                return r;
            }
            return node;
        }

        public static Dictionary<string, object> CloneDict(object node)
        {
            return Clone(node) as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public static string ReadAllText(string path)
        {
            // 版本 JSON 可能是 UTF-8（带/不带 BOM），也可能是 ANSI
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            string utf8 = Encoding.UTF8.GetString(bytes);
            if (utf8.IndexOf('�') < 0) return utf8;
            return Encoding.Default.GetString(bytes);
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    // =========================================================================
    //  全局配置 —— 对应 PCL 的 ModMinecraft.McFolderSelected 等静态状态
    // =========================================================================
    static class Cfg
    {
        public static string McFolder;        // 以 \ 结尾
        public static string PureDirectory;   // 启动器自身目录，不带尾 \

        /// <summary>
        /// ModMinecraft.OSVersion = MyWpfExtension.Computer.Info.OSVersion。
        /// Mojang 清单里会写 "os":{"version":"^10\\."} 之类的正则，必须用真实版本串。
        /// </summary>
        public static readonly string OsVersion = BuildOsVersion();

        static string BuildOsVersion()
        {
            try
            {
                var v = Environment.OSVersion.Version;
                // Unix 内核版本号在 macOS 上语义不同，Windows 上直接用 Build
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "10.0.0"; }
        }
    }

    // =========================================================================
    //  下载编排 —— 对应 PCL 的 McDownloadClientLoader
    //  五类下载物：版本 JSON / 主 jar / libraries / 资源索引 / 资源对象
    // =========================================================================
    static class Downloader
    {
        const string ManifestUrl =
            "https://launchermeta.mojang.com/mc/game/version_manifest.json";

        /// <summary>
        /// 获取全部版本清单。对应 DlClientListMojangMain（ModDownload.cs:631-661）：
        /// 官方源与镜像各取一次，versions 少于 200 条视为失败。
        /// </summary>
        public static List<VersionEntry> GetManifest()
        {
            string text = Net.GetString(ManifestUrl);
            var root = J.Parse(text) as Dictionary<string, object>;
            var versions = J.A(root, "versions");
            if (versions == null || versions.Count < 200)
                throw new Exception("版本清单异常：仅解析到 " + (versions == null ? 0 : versions.Count) + " 个版本");

            var list = new List<VersionEntry>(versions.Count);
            foreach (var node in versions)
            {
                var o = node as Dictionary<string, object>;
                if (o == null) continue;
                list.Add(new VersionEntry
                {
                    Id = J.S(o, "id"),
                    Type = J.S(o, "type"),
                    Url = J.S(o, "url"),
                    Sha1 = J.S(o, "sha1"),
                    ReleaseTime = J.S(o, "releaseTime")
                });
            }
            return list;
        }

        /// <summary>
        /// 版本 id 规范化（ModDownload.cs:761-765）：
        /// "_" → "-"，除 "1.0" 外去掉尾部 ".0"。
        /// </summary>
        public static string NormalizeId(string id)
        {
            if (id == null) return null;
            string s = id.Replace('_', '-');
            if (s != "1.0" && s.EndsWith(".0", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 2);
            return s;
        }

        public static VersionEntry FindVersion(List<VersionEntry> manifest, string id)
        {
            string norm = NormalizeId(id);
            foreach (var v in manifest)
                if (string.Equals(v.Id, norm, StringComparison.Ordinal)) return v;
            foreach (var v in manifest)
                if (string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        /// <summary>
        /// 安装原版游戏。对应 McDownloadClientLoader（ModDownloadLib.cs:1506-1631）。
        /// 返回安装后的实例目录名（默认与 id 相同）。
        /// </summary>
        public static string InstallVanilla(string id, string instanceName, bool withAssets)
        {
            instanceName = string.IsNullOrEmpty(instanceName) ? NormalizeId(id) : instanceName;

            Log.Step("获取版本清单");
            var manifest = GetManifest();
            var entry = FindVersion(manifest, id);
            if (entry == null)
                throw new Exception("版本清单里没有 " + id + "（规范化后 " + NormalizeId(id) + "）");
            Log.Info("目标版本：" + entry.Id + "（" + entry.Type + "，" + entry.ReleaseTime + "）");

            string dir = Util.Slash(Cfg.McFolder + "versions\\" + instanceName);
            string jsonPath = dir + instanceName + ".json";
            Util.EnsureDir(dir);

            // 1. 版本 JSON
            Log.Step("下载版本 JSON");
            Net.DownloadAll(new List<DownloadTask>
            {
                new DownloadTask
                {
                    Url = entry.Url,
                    MirrorUrl = Net.Mirror(entry.Url),
                    Path = jsonPath,
                    Size = -1,
                    Sha1 = entry.Sha1,
                    Label = instanceName + ".json"
                }
            }, "版本 JSON");

            // 2. 读取并计算其余下载物
            var opt = new Options { McFolder = Cfg.McFolder, PureDirectory = Cfg.PureDirectory };
            var inst = new McInstance(instanceName, opt);
            var tasks = new List<DownloadTask>();

            // 2a. 主 jar（DlClientJarGet：沿继承链下沉到最底层）
            var chain = inst.Chain();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var client = J.O(chain[i].JsonObject, "downloads", "client");
                if (client == null) continue;
                string url = J.S(client, "url");
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(new DownloadTask
                {
                    Url = url,
                    MirrorUrl = Net.Mirror(url),
                    Path = chain[i].PathVersion + chain[i].Name + ".jar",
                    Size = Math.Max(1024, J.N(client, 0, "size")),
                    Sha1 = J.S(client, "sha1"),
                    Label = chain[i].Name + ".jar"
                });
                break;
            }

            // 2b. libraries
            foreach (var lib in inst.ResolveLibraries())
            {
                if (string.IsNullOrEmpty(lib.Url)) continue;
                tasks.Add(new DownloadTask
                {
                    Url = lib.Url,
                    MirrorUrl = Net.Mirror(lib.Url),
                    Path = lib.LocalPath,
                    Size = lib.Size > 0 ? lib.Size : -1,
                    Sha1 = lib.Sha1,
                    Label = lib.Name
                });
            }

            // 2c. 资源索引
            string indexId = null, indexPath = null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var ai = J.O(chain[i].JsonObject, "assetIndex");
                if (ai == null) continue;
                indexId = J.S(ai, "id");
                string url = J.S(ai, "url");
                if (string.IsNullOrEmpty(indexId) || string.IsNullOrEmpty(url)) continue;
                indexPath = Cfg.McFolder + "assets\\indexes\\" + indexId + ".json";
                tasks.Add(new DownloadTask
                {
                    Url = url,
                    MirrorUrl = Net.Mirror(url),
                    Path = indexPath,
                    Size = J.N(ai, 0, "size") > 0 ? J.N(ai, 0, "size") : -1,
                    Sha1 = J.S(ai, "sha1"),
                    Label = "assets/indexes/" + indexId + ".json"
                });
                break;
            }

            Log.Step("下载游戏文件（" + tasks.Count + " 项）");
            Net.DownloadAll(tasks, "游戏文件");

            // 2d. 资源对象（可选，数量大，单独一轮）
            if (withAssets && indexPath != null && File.Exists(indexPath))
            {
                Log.Step("下载资源文件");
                var assetTasks = inst.AssetTasks(indexPath);
                Net.DownloadAll(assetTasks, "资源文件");
            }
            else if (!withAssets)
            {
                Log.Warn("已跳过资源文件下载（--no-assets）；首次进游戏会缺音效与语言文件");
            }

            Log.Ok("版本 " + instanceName + " 安装完成：" + dir);
            return instanceName;
        }

        /// <summary>补全已有实例的缺失文件（DlClientFix 的最小等价物）。</summary>
        public static void FixInstance(McInstance inst, bool withAssets, bool verify)
        {
            var tasks = new List<DownloadTask>();
            var chain = inst.Chain();

            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var client = J.O(chain[i].JsonObject, "downloads", "client");
                if (client == null) continue;
                string url = J.S(client, "url");
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(new DownloadTask
                {
                    Url = url,
                    MirrorUrl = Net.Mirror(url),
                    Path = chain[i].PathVersion + chain[i].Name + ".jar",
                    Size = Math.Max(1024, J.N(client, 0, "size")),
                    Sha1 = J.S(client, "sha1"),
                    Label = chain[i].Name + ".jar"
                });
                break;
            }

            foreach (var lib in inst.ResolveLibraries())
            {
                if (string.IsNullOrEmpty(lib.Url)) continue;
                tasks.Add(new DownloadTask
                {
                    Url = lib.Url,
                    MirrorUrl = Net.Mirror(lib.Url),
                    Path = lib.LocalPath,
                    Size = lib.Size > 0 ? lib.Size : -1,
                    Sha1 = lib.Sha1,
                    Label = lib.Name
                });
            }

            string indexPath = null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var ai = J.O(chain[i].JsonObject, "assetIndex");
                if (ai == null) continue;
                string id = J.S(ai, "id"), url = J.S(ai, "url");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url)) continue;
                indexPath = Cfg.McFolder + "assets\\indexes\\" + id + ".json";
                tasks.Add(new DownloadTask
                {
                    Url = url,
                    MirrorUrl = Net.Mirror(url),
                    Path = indexPath,
                    Size = J.N(ai, 0, "size") > 0 ? J.N(ai, 0, "size") : -1,
                    Sha1 = J.S(ai, "sha1"),
                    Label = "assets/indexes/" + id + ".json"
                });
                break;
            }

            int missing = 0;
            foreach (var t in tasks)
            {
                if (!File.Exists(t.Path)) { missing++; continue; }
                if (verify && !string.IsNullOrEmpty(t.Sha1))
                {
                    try { if (!Net.Sha1Of(t.Path).Equals(t.Sha1, StringComparison.OrdinalIgnoreCase)) missing++; }
                    catch { missing++; }
                }
            }
            Log.Info("待检查 " + tasks.Count + " 个文件，缺失或损坏 " + missing + " 个");
            Net.DownloadAll(tasks, "补全文件", verify);

            if (withAssets && indexPath != null && File.Exists(indexPath))
                Net.DownloadAll(inst.AssetTasks(indexPath), "资源文件");
        }
    }

    sealed class VersionEntry
    {
        public string Id;
        public string Type;
        public string Url;
        public string Sha1;
        public string ReleaseTime;
    }

    // =========================================================================
    //  运行选项
    // =========================================================================
    sealed class Options
    {
        public string McFolder;         // 以 \ 结尾
        public string PureDirectory;    // 启动器所在目录
        public string Command = "";
        public string Target = "";
        public string UserName = "Player";
        public int RamMb;               // 0 = 自动
        public int GcMode = -1;         // -1 = 默认 4（G1 增强，与 PCL LaunchAdvanceGC 一致）
        public string Server;
        public bool Fullscreen;
        public int Width = 854;
        public int Height = 480;
        public bool? Indie;             // null = 自动
        public bool Verify;
        public bool Verbose;
        public bool Offline;            // 保留：本实现固定离线登录
        public bool NoJavaDownload;
        public bool LaunchAfterInstall;
        public bool InstallOnly;

        // 本次新增
        public int Priority = 1;        // 0 = AboveNormal，2 = BelowNormal
        public string JvmArgs;          // 追加 JVM 参数
        public string GameArgs;         // 追加游戏参数
        public string VersionType;      // --versionType 的值；为空则移除该参数对
        public bool NoAssets;
        public bool NoMirror;
        public int Threads;
        public bool LogFile;
        public bool DryRun;
        public bool WaitExit;
    }

    // =========================================================================
    //  版本实例 —— 对应 PCL 的 ModMinecraft.McInstance
    // =========================================================================
    sealed class McLibEntry
    {
        public string OriginalName;
        public string Url;
        public string LocalPath;
        public long Size;
        public string Sha1;
        public bool IsNatives;

        /// <summary>McLibEntry.Name：去掉版本段（ModMinecraft.cs:1457-1469）。</summary>
        public string Name
        {
            get
            {
                if (OriginalName == null) return null;
                var parts = new List<string>(OriginalName.Split(':'));
                if (parts.Count < 3) return OriginalName;
                parts.RemoveAt(2);
                return string.Join(":", parts);
            }
        }

        public override string ToString()
        {
            return (IsNatives ? "[Native] " : "") + LocalPath;
        }
    }

    sealed class McVersionInfo
    {
        public string VanillaName;
        public System.Version Vanilla;
        public string OptiFine;
        public string Forge;
        public string NeoForge;
        public string Fabric;
        public bool HasLiteLoader;

        public bool HasOptiFine { get { return OptiFine != null; } }
        public bool HasForge { get { return Forge != null; } }
        public bool HasNeoForge { get { return NeoForge != null; } }
        public bool HasFabric { get { return Fabric != null; } }
        public bool HasForgelike { get { return HasForge || HasNeoForge; } }

        /// <summary>Vanilla.Major &lt; 1000：即版本号被成功识别。</summary>
        public bool Vaild { get { return Vanilla != null && Vanilla.Major < 1000; } }

        public string DisplayName()
        {
            var sb = new StringBuilder();
            if (HasForge) sb.Append(", Forge").Append(Forge == "未知版本" ? "" : " " + Forge);
            if (HasNeoForge) sb.Append(", NeoForge").Append(NeoForge == "未知版本" ? "" : " " + NeoForge);
            if (HasFabric) sb.Append(", Fabric").Append(Fabric == "未知版本" ? "" : " " + Fabric);
            if (HasOptiFine) sb.Append(", OptiFine").Append(OptiFine == "未知版本" ? "" : " " + OptiFine.Replace("-", " ").Replace("_", " "));
            if (HasLiteLoader) sb.Append(", LiteLoader");
            return (VanillaName ?? "Unknown") + sb;
        }
    }

    sealed class McInstance
    {
        public readonly string Name;
        public readonly string PathVersion;   // 以 \ 结尾
        public string InheritName = "";
        readonly Options _opt;
        readonly string _text;               // 原始 JSON 文本（PCL 的 JsonText）
        readonly Dictionary<string, object> _raw;
        Dictionary<string, object> _merged;

        public McInstance(string name, Options opt)
        {
            Name = name;
            _opt = opt;
            PathVersion = Util.Slash(opt.McFolder + "versions\\" + name);
            string jsonPath = PathVersion + name + ".json";
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("未找到版本 JSON：" + jsonPath);

            string text = Util.ReadAllText(jsonPath);
            _text = text;
            var parsed = J.Parse(text) as Dictionary<string, object>;
            if (parsed == null)
                throw new Exception("版本 JSON 解析失败：" + jsonPath);

            // HMCL 多分支格式（ModMinecraft.cs:451-488）
            if (parsed.ContainsKey("patches") && !parsed.ContainsKey("time"))
            {
                var patches = J.A(parsed, "patches");
                if (patches != null && patches.Count > 0)
                {
                    List<object> sorted = patches
                        .OrderBy(p => J.N(p, 0, "priority"))
                        .ToList();
                    Dictionary<string, object> val = null;
                    foreach (var p in sorted)
                    {
                        var po = p as Dictionary<string, object>;
                        if (po == null || !po.ContainsKey("id")) continue;
                        if (val == null) val = Util.CloneDict(po);
                        else MergeInto(val, po);
                    }
                    if (val != null)
                    {
                        val["id"] = name;
                        val.Remove("inheritsFrom");
                        parsed = val;
                    }
                }
            }

            _raw = parsed;
            string inh = J.Str(J.Get(parsed, "inheritsFrom"));
            if (string.Equals(inh, name, StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("自引用的继承版本：" + name + "，已忽略");
                inh = "";
            }
            InheritName = inh ?? "";
        }

        /// <summary>
        /// 合并后的扁平 JSON。语义与 ModMinecraft.cs:489-517 一致：
        /// 其余字段父层为底、子层覆盖；libraries 为「子层在前、父层追加」。
        /// </summary>
        public Dictionary<string, object> JsonObject
        {
            get
            {
                if (_merged != null) return _merged;

                var cur = Util.CloneDict(_raw);
                string inh = InheritName;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Name };
                while (!string.IsNullOrEmpty(inh))
                {
                    if (!seen.Add(inh))
                        throw new Exception("版本依赖出现循环：" + inh);

                    var parent = new McInstance(inh, _opt);
                    if (string.Equals(parent.InheritName, inh, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("版本依赖项出现嵌套：" + inh);

                    var childLibs = Util.Clone(J.Get(cur, "libraries")) as List<object>
                                    ?? new List<object>();
                    var parentLibs = J.A(parent.JsonObject, "libraries") ?? new List<object>();
                    foreach (var lib in parentLibs) childLibs.Add(Util.Clone(lib));

                    var merged = Util.CloneDict(parent.JsonObject);
                    MergeInto(merged, cur);
                    merged["libraries"] = childLibs;
                    cur = merged;

                    inh = parent.InheritName;
                }
                _merged = cur;
                return cur;
            }
        }

        /// <summary>
        /// Newtonsoft JObject.Merge 的等价实现：对象递归合并，数组拼接（父在前、子在后），
        /// 标量由来源覆盖；来源中的 null 被忽略（默认 MergeNullValueHandling.Ignore）。
        /// </summary>
        static void MergeInto(Dictionary<string, object> target, Dictionary<string, object> source)
        {
            foreach (var kv in source)
            {
                if (kv.Value == null) continue;
                object old;
                if (target.TryGetValue(kv.Key, out old))
                {
                    var od = old as Dictionary<string, object>;
                    var nd = kv.Value as Dictionary<string, object>;
                    if (od != null && nd != null) { MergeInto(od, nd); continue; }

                    var ol = old as List<object>;
                    var nl = kv.Value as List<object>;
                    if (ol != null && nl != null)
                    {
                        var combined = new List<object>(ol.Count + nl.Count);
                        foreach (var item in ol) combined.Add(Util.Clone(item));
                        foreach (var item in nl) combined.Add(Util.Clone(item));
                        target[kv.Key] = combined;
                        continue;
                    }
                }
                target[kv.Key] = Util.Clone(kv.Value);
            }
        }

        public DateTime ReleaseTime
        {
            get
            {
                string s = J.S(JsonObject, "releaseTime");
                DateTime dt;
                if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    return dt;
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// 原版版本号（McInstance.Version.VanillaName → McVersion.NameToVersion）。
        /// PCL 的推导顺序（ModMinecraft.cs:189-300）：
        ///   type=pending → clientVersion → patches[id=game].version →
        ///   arguments.game 中 --fml.mcVersion 的下一项 → jar/inheritsFrom →
        ///   从 downloads 的 launcher.mojang.com URL 提取 → 从 libraries 提取 →
        ///   jar → 版本 jar 内 version.json → 从实例名正则提取。
        /// 直接读目录名是错的：整合包目录名不含版本号。
        /// </summary>
        public string VanillaName
        {
            get
            {
                if (_vanillaName != null) return _vanillaName;
                _vanillaName = DeriveVanillaName();
                return _vanillaName;
            }
        }

        string _vanillaName;

        string DeriveVanillaName()
        {
            var obj = JsonObject;

            string type = J.S(obj, "type") ?? "";
            if (string.Equals(type, "pending", StringComparison.Ordinal))
                return "pending";

            string cv = J.S(obj, "clientVersion");
            if (!string.IsNullOrEmpty(cv)) return cv;

            var patches = J.A(obj, "patches");
            if (patches != null)
            {
                foreach (var p in patches)
                {
                    var po = p as Dictionary<string, object>;
                    if (po == null) continue;
                    if (!string.Equals(J.S(po, "id") ?? "", "game", StringComparison.Ordinal)) continue;
                    string v = J.S(po, "version");
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }

            var gameArgs = J.A(obj, "arguments", "game");
            if (gameArgs != null)
            {
                for (int i = 0; i < gameArgs.Count - 1; i++)
                {
                    if (string.Equals(gameArgs[i] as string, "--fml.mcVersion", StringComparison.Ordinal))
                        return gameArgs[i + 1] as string ?? "";
                }
            }

            if (!string.IsNullOrEmpty(InheritName))
            {
                string jar = J.S(obj, "jar");
                return string.IsNullOrEmpty(jar) ? InheritName : jar;
            }

            string dl = J.Get(obj, "downloads") != null ? J.Get(obj, "downloads").ToString() : "";
            var m = Regex.Match(dl, "(?<=launcher\\.mojang\\.com/mc/game/)[^/]*");
            if (m.Success) return m.Value;

            string libs = J.Get(obj, "libraries") != null ? J.Get(obj, "libraries").ToString() : "";
            m = Regex.Match(libs, "(?<=net\\.minecraftforge:forge:)[0-9]{1,2}\\.[0-9+.]+");
            if (!m.Success) m = Regex.Match(libs, "(?<=net\\.minecraftforge:fmlloader:)[0-9]{1,2}\\.[0-9+.]+");
            if (!m.Success) m = Regex.Match(libs, "(?<=optifine:OptiFine:)[0-9]{1,2}\\.[0-9+.]+");
            if (!m.Success) m = Regex.Match(libs, "(?<=((fabricmc)|(quiltmc)):intermediary:)[^\"]*");
            if (m.Success) return m.Value;

            string jarField = J.S(obj, "jar");
            if (!string.IsNullOrEmpty(jarField)) return jarField;

            // 最后的兜底：从实例名里找版本号
            m = Regex.Match(Name, "(1|[2-9][0-9])\\.[0-9]+(\\.[0-9]+)?");
            return m.Success ? m.Value : "Unknown";
        }

        /// <summary>McVersion.NameToVersion（ModMinecraft.cs:1354-1400）。</summary>
        public static System.Version NameToVersion(string name)
        {
            if (name == null) return new System.Version(9999, 0, 0);
            name = name.ToLowerInvariant().Replace("_unobfuscated", "").Replace(" unobfuscated", "");

            switch (name)
            {
                case "2.0": return NameToVersion("1.5.1");
                case "15w14a": return NameToVersion("1.8.3");
                case "24w14potato": return NameToVersion("1.20.4");
                case "25w14craftmine": return NameToVersion("1.21.4");
                case "26w14a": return NameToVersion("26.1.1");
            }
            if (name.Contains(".rv-pre")) return NameToVersion("1.9.2");
            if (name.Contains("shareware")) return NameToVersion("1.13.2");
            if (name.StartsWith("20w14", StringComparison.Ordinal) && name != "20w14a") return NameToVersion("1.15.2");
            if (name.Contains("oneblockatatime")) return NameToVersion("1.18.2");
            if (name.Contains("23w13a")) return NameToVersion("1.19.4");

            string[] parts = name.Split(new[] { ' ', '_', '-', '.' });
            if (name.StartsWith("1.", StringComparison.Ordinal))
                return Ver(Num(parts, 1), 0, Num(parts, 2));
            if (Regex.IsMatch(name, "^[2-9][0-9]\\."))
                return Ver(Num(parts, 0), Num(parts, 1), Num(parts, 2));
            return new System.Version(9999, 0, 0);
        }

        static int Num(string[] parts, int i)
        {
            if (i >= parts.Length) return 0;
            double d;
            return double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                ? (int)Math.Round(d) : 0;
        }

        static System.Version Ver(int a, int b, int c)
        {
            return new System.Version(a, b, c < 0 ? 0 : c);
        }

        /// <summary>
        /// 版本标识与加载器识别（ModMinecraft.cs:815-847 的 Load()）。
        /// 注意 PCL 的判定基于 JSON 原始文本的 Contains，而不是结构化字段。
        /// </summary>
        public McVersionInfo Version
        {
            get
            {
                if (_version != null) return _version;
                string text = _text ?? "";
                string vn = VanillaName;
                var v = new McVersionInfo { VanillaName = vn, Vanilla = NameToVersion(vn) };

                if (text.Contains("optifine"))
                    v.OptiFine = RegexSeek(text, "(?<=HD_U_)[^\":/]+") ?? "未知版本";
                if (text.Contains("liteloader"))
                    v.HasLiteLoader = true;

                if (text.Contains("net.fabricmc:fabric-loader") || text.Contains("org.quiltmc:quilt-loader"))
                {
                    v.Fabric = (RegexSeek(text, "(?<=(net.fabricmc:fabric-loader:)|(org.quiltmc:quilt-loader:))[0-9\\.]+(\\+build.[0-9]+)?")
                                ?? "未知版本").Replace("+build", "");
                }
                else if (text.Contains("minecraftforge") && !text.Contains("net.neoforge"))
                {
                    v.Forge = RegexSeek(text, "(?<=forge:[0-9\\.]+(_pre[0-9]*)?\\-)[0-9\\.]+")
                           ?? RegexSeek(text, "(?<=net\\.minecraftforge:minecraftforge:)[0-9\\.]+")
                           ?? RegexSeek(text, "(?<=net\\.minecraftforge:fmlloader:[0-9\\.]+-)[0-9\\.]+")
                           ?? "未知版本";
                }
                else if (text.Contains("net.neoforge"))
                {
                    v.NeoForge = RegexSeek(text, "(?<=orgeVersion\",[^\"]*?\")[^\"]+(?=\",)") ?? "未知版本";
                }
                _version = v;
                return v;
            }
        }

        McVersionInfo _version;

        static string RegexSeek(string input, string pattern)
        {
            if (string.IsNullOrEmpty(input)) return null;
            try
            {
                var m = Regex.Match(input, pattern);
                return m.Success ? m.Value : null;
            }
            catch { return null; }
        }

        public bool Valid { get { return Version.Vaild; } }

        public int JavaMajor
        {
            get
            {
                long v = J.N(JsonObject, 0, "javaVersion", "majorVersion");
                if (v > 0) return (int)v;
                v = J.N(JsonObject, 0, "java_version");
                return (int)v;
            }
        }

        public string JavaComponent
        {
            get
            {
                string c = J.S(JsonObject, "javaVersion", "component");
                if (string.IsNullOrEmpty(c)) c = J.S(JsonObject, "java_component");
                return c;
            }
        }

        public string MainClass { get { return J.S(JsonObject, "mainClass"); } }

        /// <summary>
        /// 版本隔离目录（ModMinecraft.cs:103-146）。
        /// 显式配置优先；否则版本目录下已有 mods 文件或 saves 子目录时自动开启；再否则用全局默认。
        /// </summary>
        public string PathIndie
        {
            get
            {
                bool indie;
                if (_opt.Indie.HasValue) indie = _opt.Indie.Value;
                else
                {
                    bool hasMods = false, hasSaves = false;
                    try
                    {
                        string mods = PathVersion + "mods\\";
                        if (Directory.Exists(mods)) hasMods = Directory.EnumerateFiles(mods).Any();
                        string saves = PathVersion + "saves\\";
                        if (Directory.Exists(saves)) hasSaves = Directory.EnumerateDirectories(saves).Any();
                    }
                    catch { }
                    if (hasMods || hasSaves) indie = true;
                    else indie = true;   // 全局默认：开启隔离（与 PCL 默认一致）
                }
                return indie ? PathVersion : Util.Slash(_opt.McFolder);
            }
        }

        // ---------------------- 依赖库解析 ----------------------

        /// <summary>McLibGet（ModMinecraft.cs:3072-3077）。</summary>
        public static string McLibGet(string original, bool withHead)
        {
            if (string.IsNullOrEmpty(original)) return null;
            string[] a = original.Split(':');
            if (a.Length < 3) return original;
            var tail = new List<string>();
            for (int i = 1; i < a.Length; i++) tail.Add(a[i]);
            string rel = a[0].Replace(".", "\\") + "\\" + a[1] + "\\" + a[2] + "\\" +
                         string.Join("-", tail.ToArray()) + ".jar";
            return (withHead ? Cfg.McFolder + "libraries\\" : "") + rel;
        }

        /// <summary>McJsonRuleCheck（ModMinecraft.cs:2615-2678）。</summary>
        public static bool McJsonRuleCheck(object ruleToken)
        {
            var rules = ruleToken as List<object>;
            if (rules == null) return true;

            bool result = false;
            foreach (var item in rules)
            {
                var rule = item as Dictionary<string, object>;
                if (rule == null) continue;
                bool flag = true;

                var os = J.O(rule, "os");
                if (os != null)
                {
                    string osName = J.S(os, "name");
                    if (osName != null && !string.Equals(osName, "unknown", StringComparison.Ordinal))
                    {
                        if (string.Equals(osName, "windows", StringComparison.Ordinal))
                        {
                            string verRe = J.S(os, "version");
                            if (verRe != null)
                            {
                                try { flag = flag && Regex.IsMatch(Cfg.OsVersion, verRe); }
                                catch { }
                            }
                        }
                        else flag = false;
                    }

                    object archNode = J.Get(os, "arch");
                    if (archNode != null)
                    {
                        string arch = archNode as string;
                        if (arch != null)
                            flag = flag && !string.Equals(arch, "x86", StringComparison.Ordinal)
                                        && arch.IndexOf("arm", StringComparison.OrdinalIgnoreCase) < 0;
                        else
                        {
                            // 少数清单里 arch 是数组
                            var arches = archNode as List<object>;
                            if (arches != null)
                            {
                                foreach (var a in arches)
                                {
                                    string s = a as string;
                                    if (s == null) continue;
                                    if (string.Equals(s, "x86", StringComparison.Ordinal) ||
                                        s.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0) flag = false;
                                }
                            }
                        }
                    }
                }

                var features = J.O(rule, "features");
                if (features != null)
                {
                    if (features.ContainsKey("is_demo_user")) flag = false;
                    foreach (var key in features.Keys)
                        if (key.IndexOf("quick_play", StringComparison.OrdinalIgnoreCase) >= 0) flag = false;
                }

                string action = J.S(rule, "action");
                if (string.Equals(action, "allow", StringComparison.Ordinal))
                {
                    if (flag) result = true;
                }
                else if (flag) result = false;
            }
            return result;
        }

        static Dictionary<string, object> StripNulls(Dictionary<string, object> d)
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in d) if (kv.Value != null) r[kv.Key] = kv.Value;
            return r;
        }

        /// <summary>版本文本比较（CompareVersion，ModMinecraft.cs:3318+）。</summary>
        public static int CompareVersion(string left, string right)
        {
            if (left == null) left = "";
            if (right == null) right = "";
            string l = left.ToLowerInvariant(), r = right.ToLowerInvariant();
            var tl = Regex.Matches(l, "[a-z]+|[0-9]+").Cast<Match>().Select(m => m.Value).ToList();
            var tr = Regex.Matches(r, "[a-z]+|[0-9]+").Cast<Match>().Select(m => m.Value).ToList();
            int n = Math.Max(tl.Count, tr.Count);
            for (int i = 0; i < n; i++)
            {
                if (i >= tl.Count) return -1;
                if (i >= tr.Count) return 1;
                string a = tl[i], b = tr[i];
                long la, lb;
                bool na = long.TryParse(a, out la);
                bool nb = long.TryParse(b, out lb);
                if (na && nb)
                {
                    if (la != lb) return la > lb ? 1 : -1;
                }
                else
                {
                    int c = string.CompareOrdinal(a, b);
                    if (c != 0) return c > 0 ? 1 : -1;
                }
            }
            int x = string.CompareOrdinal(l, r);
            return x == 0 ? 0 : (x > 0 ? 1 : -1);
        }

        static string VersionDir(string localPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(localPath);
                return dir == null ? "" : Path.GetFileName(dir);
            }
            catch { return ""; }
        }

        /// <summary>McLibListGetWithJson（ModMinecraft.cs:2734-2897）。</summary>
        public List<McLibEntry> ResolveLibraries()
        {
            var result = new List<McLibEntry>();
            var libs = J.A(JsonObject, "libraries");
            if (libs == null) return result;

            foreach (var node in libs)
            {
                var raw = node as Dictionary<string, object>;
                if (raw == null) continue;
                var v = StripNulls(raw);

                if (!McJsonRuleCheck(J.Get(v, "rules"))) continue;

                string name = J.S(v, "name");
                if (string.IsNullOrEmpty(name)) continue;

                string baseUrl = J.S(v, "url");
                if (baseUrl != null) baseUrl += McLibGet(name, false).Replace("\\", "/");

                if (J.Get(v, "natives") == null)
                {
                    string localPath = McLibGet(name, true);
                    var art = J.O(v, "downloads", "artifact");
                    try
                    {
                        if (art != null)
                        {
                            string p = J.S(art, "path");
                            result.Add(new McLibEntry
                            {
                                OriginalName = name,
                                Url = J.S(art, "url") ?? baseUrl,
                                LocalPath = p == null ? localPath
                                                      : Cfg.McFolder + "libraries\\" + p.Replace("/", "\\"),
                                Size = J.N(art, 0, "size"),
                                Sha1 = J.S(art, "sha1"),
                                IsNatives = false
                            });
                        }
                        else
                        {
                            result.Add(new McLibEntry
                            {
                                OriginalName = name,
                                Url = baseUrl,
                                LocalPath = localPath,
                                Size = 0,
                                Sha1 = null,
                                IsNatives = false
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("处理支持库失败（无 Natives，" + name + "）：" + ex.Message);
                        result.Add(new McLibEntry
                        {
                            OriginalName = name,
                            Url = baseUrl,
                            LocalPath = localPath,
                            Size = 0,
                            Sha1 = null,
                            IsNatives = false
                        });
                    }
                }
                else
                {
                    string classifier = J.S(v, "natives", "windows");
                    if (classifier == null) continue;

                    string mavenPath = McLibGet(name, true)
                        .Replace(".jar", "-" + classifier + ".jar")
                        .Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");

                    try
                    {
                        var cls = J.O(v, "downloads", "classifiers", classifier);
                        if (cls != null)
                        {
                            string p = J.S(cls, "path");
                            result.Add(new McLibEntry
                            {
                                OriginalName = name,
                                Url = J.S(cls, "url") ?? baseUrl,
                                LocalPath = p == null ? mavenPath
                                                      : Cfg.McFolder + "libraries\\" + p.Replace("/", "\\"),
                                Size = J.N(cls, 0, "size"),
                                Sha1 = J.S(cls, "sha1"),
                                IsNatives = true
                            });
                        }
                        else
                        {
                            result.Add(new McLibEntry
                            {
                                OriginalName = name,
                                Url = baseUrl,
                                LocalPath = mavenPath,
                                Size = 0,
                                Sha1 = null,
                                IsNatives = true
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("处理支持库失败（有 Natives，" + name + "）：" + ex.Message);
                        result.Add(new McLibEntry
                        {
                            OriginalName = name,
                            Url = baseUrl,
                            LocalPath = mavenPath,
                            Size = 0,
                            Sha1 = null,
                            IsNatives = true
                        });
                    }
                }
            }

            // 同名去重：键为 Name+IsNatives，版本更高者胜（ModMinecraft.cs:2868-2895）
            var map = new Dictionary<string, McLibEntry>(StringComparer.Ordinal);
            foreach (var e in result)
            {
                string key = e.Name + e.IsNatives;
                McLibEntry old;
                if (map.TryGetValue(key, out old))
                {
                    if (CompareVersion(VersionDir(e.LocalPath), VersionDir(old.LocalPath)) >= 0)
                        map[key] = e;
                }
                else map[key] = e;
            }
            return new List<McLibEntry>(map.Values);
        }

        /// <summary>是否是 HMCL 多分支格式（ModMinecraft.IsHmclFormatJson）。</summary>
        public bool IsHmclFormatJson
        {
            get { return _raw.ContainsKey("patches") && !_raw.ContainsKey("time"); }
        }

        /// <summary>
        /// 主 jar 所属实例（McLibListGet，ModMinecraft.cs:2680-2732）。
        /// 默认沿继承链下沉到最底层；但 Forge 类且原版 &gt;= 1.17 时不下沉，
        /// 因为此时 Forge 的 jar 才是真正的启动入口（愚者 1.20.1 即走这一支）。
        /// 显式 jar 字段优先。
        /// </summary>
        public McInstance MainJarInstance()
        {
            string jarField = J.S(JsonObject, "jar");
            if (!IsHmclFormatJson && !string.IsNullOrEmpty(jarField))
            {
                var named = TryCreate(jarField);
                if (named != null) return named;
            }

            var cur = this;
            var v = Version;
            bool sink = !v.HasForgelike || !v.Vaild || v.Vanilla.Major < 17;
            if (sink)
            {
                while (!string.IsNullOrEmpty(cur.InheritName) &&
                       !string.Equals(cur.InheritName, cur.Name, StringComparison.Ordinal))
                {
                    var parent = cur.TryCreate(cur.InheritName);
                    if (parent == null) break;
                    cur = parent;
                }
            }
            return cur;
        }

        McInstance TryCreate(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return null;
                string json = Util.Slash(_opt.McFolder + "versions\\" + name) + name + ".json";
                return File.Exists(json) ? new McInstance(name, _opt) : null;
            }
            catch { return null; }
        }

        public string MainJarPath
        {
            get
            {
                var target = MainJarInstance();
                return target.PathVersion + target.Name + ".jar";
            }
        }

        /// <summary>
        /// 启动用的库列表（McLibListGet(IncludeMainJar)）：非 natives 库 + 主 jar 追加在末尾。
        /// 注意主 jar 追加在**最后**，而 classpath 里 OptiFine 会被插到倒数第二位。
        /// </summary>
        public List<McLibEntry> LibListGet(bool includeMainJar)
        {
            var list = ResolveLibraries();
            if (includeMainJar)
            {
                var target = MainJarInstance();
                var client = J.O(target.JsonObject, "downloads", "client");
                list.Add(new McLibEntry
                {
                    OriginalName = null,
                    LocalPath = target.PathVersion + target.Name + ".jar",
                    Size = 0,
                    Sha1 = J.S(client, "sha1"),
                    Url = J.S(client, "url"),
                    IsNatives = false
                });
            }
            return list;
        }

        /// <summary>沿继承链找到最底层含 downloads.client 的版本（DlClientJarGet，ModDownload.cs:378-415）。</summary>
        public string Deprecated_MainJarPath
        {
            get
            {
                var chain = Chain();
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    if (J.O(chain[i].JsonObject, "downloads", "client") != null)
                        return chain[i].PathVersion + chain[i].Name + ".jar";
                }
                return PathVersion + Name + ".jar";
            }
        }

        /// <summary>自身 → 父 → 祖父 的实例链。</summary>
        public List<McInstance> Chain()
        {
            var list = new List<McInstance>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cur = this;
            while (cur != null)
            {
                if (!seen.Add(cur.Name)) break;
                list.Add(cur);
                cur = string.IsNullOrEmpty(cur.InheritName) ? null : new McInstance(cur.InheritName, _opt);
            }
            return list;
        }

        public string AssetsIndexName
        {
            get
            {
                string id = J.S(JsonObject, "assetIndex", "id");
                if (id != null) return id;
                string a = J.S(JsonObject, "assets");
                if (a != null) return a;
                return "legacy";
            }
        }

        /// <summary>资源文件清单（McAssetsListGet，ModMinecraft.cs:3156-3202）。</summary>
        public List<DownloadTask> AssetTasks(string indexPath)
        {
            var list = new List<DownloadTask>();
            var index = J.Parse(Util.ReadAllText(indexPath)) as Dictionary<string, object>;
            if (index == null) throw new Exception("资源索引解析失败：" + indexPath);

            bool mapToResources = J.Bool(J.Get(index, "map_to_resources"));
            bool isVirtual = J.Bool(J.Get(index, "virtual"));
            var objects = J.O(index, "objects");
            if (objects == null) return list;

            foreach (var kv in objects)
            {
                var o = kv.Value as Dictionary<string, object>;
                if (o == null) continue;
                string hash = J.S(o, "hash");
                long size = J.N(o, 0, "size");
                if (string.IsNullOrEmpty(hash)) continue;

                string local;
                if (mapToResources)
                    local = PathIndie + "resources\\" + kv.Key;
                else if (isVirtual)
                    local = Cfg.McFolder + "assets\\virtual\\legacy\\" + kv.Key;
                else
                    local = Cfg.McFolder + "assets\\objects\\" + hash.Substring(0, 2) + "\\" + hash;
                local = local.Replace("/", "\\");

                string url = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash;
                list.Add(new DownloadTask
                {
                    Url = url,
                    MirrorUrl = Net.Mirror(url),
                    Path = local,
                    Size = size,
                    Sha1 = hash,
                    Label = kv.Key
                });
            }
            return list;
        }
    }

    // =========================================================================
    //  Java —— 版本要求推导、本机探测、Mojang 运行时下载
    // =========================================================================

    /// <summary>值范围，对应 PCL 的 ValueRange&lt;Version&gt;（只实现启动链路用到的部分）。</summary>
    sealed class VersionRange
    {
        public System.Version Lower;      // null = 无下界
        public bool LowerInclusive = true;
        public System.Version Upper;      // null = 无上界
        public bool UpperInclusive = true;

        public static VersionRange All() { return new VersionRange(); }

        public static VersionRange AtLeast(System.Version v)
        {
            return new VersionRange { Lower = v, LowerInclusive = true };
        }

        public static VersionRange LessThan(System.Version v)
        {
            return new VersionRange { Upper = v, UpperInclusive = false };
        }

        public static VersionRange ClosedOpen(System.Version lo, System.Version hi)
        {
            return new VersionRange { Lower = lo, LowerInclusive = true, Upper = hi, UpperInclusive = false };
        }

        public static VersionRange AtMost(System.Version v)
        {
            return new VersionRange { Upper = v, UpperInclusive = true };
        }

        public bool Contains(System.Version v)
        {
            if (v == null) return false;
            if (Lower != null)
            {
                int c = v.CompareTo(Lower);
                if (c < 0) return false;
                if (c == 0 && !LowerInclusive) return false;
            }
            if (Upper != null)
            {
                int c = v.CompareTo(Upper);
                if (c > 0) return false;
                if (c == 0 && !UpperInclusive) return false;
            }
            return true;
        }

        public bool IsEmpty()
        {
            if (Lower == null || Upper == null) return false;
            int c = Lower.CompareTo(Upper);
            if (c > 0) return true;
            if (c == 0 && !(LowerInclusive && UpperInclusive)) return true;
            return false;
        }

        public VersionRange Intersect(VersionRange other)
        {
            if (other == null) return this;
            var r = new VersionRange();
            if (Lower == null) { r.Lower = other.Lower; r.LowerInclusive = other.LowerInclusive; }
            else if (other.Lower == null) { r.Lower = Lower; r.LowerInclusive = LowerInclusive; }
            else
            {
                int c = Lower.CompareTo(other.Lower);
                if (c > 0) { r.Lower = Lower; r.LowerInclusive = LowerInclusive; }
                else if (c < 0) { r.Lower = other.Lower; r.LowerInclusive = other.LowerInclusive; }
                else { r.Lower = Lower; r.LowerInclusive = LowerInclusive && other.LowerInclusive; }
            }
            if (Upper == null) { r.Upper = other.Upper; r.UpperInclusive = other.UpperInclusive; }
            else if (other.Upper == null) { r.Upper = Upper; r.UpperInclusive = UpperInclusive; }
            else
            {
                int c = Upper.CompareTo(other.Upper);
                if (c < 0) { r.Upper = Upper; r.UpperInclusive = UpperInclusive; }
                else if (c > 0) { r.Upper = other.Upper; r.UpperInclusive = other.UpperInclusive; }
                else { r.Upper = Upper; r.UpperInclusive = UpperInclusive && other.UpperInclusive; }
            }
            return r;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Lower != null) sb.Append(LowerInclusive ? "[" : "(").Append(Lower);
            else sb.Append("(-∞");
            sb.Append(", ");
            if (Upper != null) sb.Append(Upper).Append(UpperInclusive ? "]" : ")");
            else sb.Append("+∞)");
            return sb.ToString();
        }
    }

    sealed class JavaInfo
    {
        public string ExePath;
        public string Folder;
        public System.Version Version;
        public bool MojangRuntime;

        /// <summary>Java 的 file.encoding 属性（PCLCS.Java.FileEncodingName）。</summary>
        public string FileEncodingName;

        /// <summary>Java 的 native.encoding 属性，缺失时退回 file.encoding（PCLCS.Java.NativeEncodingName）。</summary>
        public string NativeEncodingName;

        public override string ToString()
        {
            return "Java " + (Version == null ? "?" : Version.ToString()) + "：" + Folder;
        }
    }

    static class Java
    {
        const string RuntimeManifest =
            "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

        /// <summary>PCL 明确跳过的三个 sha1（ModJava.cs:993），均为 legal/* 文本文件。</summary>
        static readonly HashSet<string> Blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "12976a6c2b227cbac58969c1455444596c894656",
            "c80e4bab46e34d02826eab226a4441d0970f2aba",
            "84d2102ad171863db04e7ee22a259d1f6c5de4a5",
        };

        // ---------------------------------------------------------------------
        //  版本要求推导（ModJava.cs:296-511）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 返回 (范围, 推荐组件名)。语义逐条对应 GetJavaRequirement。
        /// 注意 PCL 的写法：条件链是 if/else if，只有第一条命中的发布日期/版本号规则生效；
        /// 后续的加载器修正与 javaVersion 约束都会再 Intersect 一次，交集为空时退回该单条约束。
        /// </summary>
        public static VersionRange GetRequirement(McInstance inst, out string component)
        {
            component = null;
            var range = VersionRange.All();
            var v = inst.Version;

            Action<VersionRange> add = c =>
            {
                var r = range.Intersect(c);
                if (r.IsEmpty()) range = c;   // PCL：冲突时退回最新一条约束
                else range = r;
            };

            DateTime release = inst.ReleaseTime;
            if ((!v.Vaild && release >= new DateTime(2024, 4, 2)) || (v.Vaild && v.Vanilla >= new System.Version(20, 0, 5)))
                add(VersionRange.AtLeast(new System.Version(21, 0)));
            else if ((!v.Vaild && release >= new DateTime(2021, 11, 16)) || (v.Vaild && v.Vanilla.Major >= 18))
                add(VersionRange.AtLeast(new System.Version(17, 0)));
            else if ((!v.Vaild && release >= new DateTime(2021, 5, 11)) || (v.Vaild && v.Vanilla.Major >= 17))
                add(VersionRange.AtLeast(new System.Version(16, 0)));
            else if (release.Year >= 2017)
                add(VersionRange.AtLeast(new System.Version(8, 0)));
            else if (release <= new DateTime(2013, 5, 1) && release.Year >= 2001)
                add(VersionRange.LessThan(new System.Version(9, 0)));

            int major = inst.JavaMajor;
            if (major >= 22)
            {
                add(VersionRange.AtLeast(new System.Version(major, 0)));
                string c = inst.JavaComponent;
                if (!string.IsNullOrEmpty(c)) component = c;
            }

            if (v.HasOptiFine && v.Vaild)
            {
                if (v.Vanilla.Major < 7) add(VersionRange.LessThan(new System.Version(9, 0)));
                else if (v.Vanilla.Major >= 8 && v.Vanilla.Major < 12) add(VersionRange.ClosedOpen(new System.Version(8, 0), new System.Version(9, 0)));
                else if (v.Vanilla.Major == 12) add(VersionRange.LessThan(new System.Version(9, 0)));
            }

            if (v.HasForge)
            {
                if (v.Vanilla != null && v.Vanilla >= new System.Version(6, 0, 1) && v.Vanilla <= new System.Version(7, 0, 2))
                    add(VersionRange.ClosedOpen(new System.Version(7, 0), new System.Version(8, 0)));
                else if (!v.Vaild || v.Vanilla.Major <= 12)
                    add(VersionRange.LessThan(new System.Version(9, 0)));
                else if (v.Vanilla.Major <= 14)
                    add(VersionRange.ClosedOpen(new System.Version(8, 0), new System.Version(11, 0)));
                else if (v.Vanilla.Major == 15)
                    add(VersionRange.ClosedOpen(new System.Version(8, 0), new System.Version(16, 0)));
                else if (CompareForge(v.Forge, "34.0.0") >= 0 && CompareForge("36.2.25", v.Forge) >= 0)
                    add(VersionRange.AtMost(new System.Version(8, 0, 320)));
                else if (CompareForge(v.Forge, "36.2.26") >= 0 && CompareForge("36.999999.999999", v.Forge) >= 0)
                    add(VersionRange.LessThan(new System.Version(24, 0)));
                else if (CompareForge(v.Forge, "37.0.0") >= 0 && CompareForge("37.0.79", v.Forge) >= 0)
                    add(VersionRange.LessThan(new System.Version(17, 0)));
                else if (v.Vanilla.Major == 18 && v.HasOptiFine)
                    add(VersionRange.LessThan(new System.Version(19, 0)));
                else if (CompareForge(v.Forge, "45.0.21") >= 0 && CompareForge("45.0.65", v.Forge) >= 0)
                    add(VersionRange.LessThan(new System.Version(20, 0)));
                else if (CompareForge(v.Forge, "45.0.66") >= 0 && CompareForge("47.4.8", v.Forge) >= 0)
                    add(VersionRange.LessThan(new System.Version(22, 0)));
            }

            if (v.HasNeoForge &&
                ((v.Vanilla != null && v.Vanilla == new System.Version(20, 0, 1)) ||
                 (CompareForge("20.2.62-beta", v.NeoForge) >= 0 && v.NeoForge.IndexOf("25w14craftmine", StringComparison.Ordinal) < 0)))
                add(VersionRange.LessThan(new System.Version(22, 0)));

            if (v.HasFabric && v.Vaild)
            {
                if (v.Vanilla.Major >= 15 && v.Vanilla.Major <= 16) add(VersionRange.AtLeast(new System.Version(8, 0)));
                else if (v.Vanilla.Major >= 18) add(VersionRange.AtLeast(new System.Version(17, 0)));
            }

            if (v.HasLiteLoader && v.Vaild)
                add(VersionRange.LessThan(new System.Version(9, 0)));

            return range;
        }

        /// <summary>Forge/NeoForge 版本比较，复用 PCL 的 CompareVersion（"未知版本" 视为最高）。</summary>
        static int CompareForge(string left, string right)
        {
            return McInstance.CompareVersion(left, right);
        }

        // ---------------------------------------------------------------------
        //  本机探测
        // ---------------------------------------------------------------------

        /// <summary>
        /// 扫描本机 Java。对应 JavaUtils.RefreshListAsync 的可观察结果
        /// （PCL 日志中的来源：Program Files、.jdks、.minecraft\runtime、PATH）。
        /// 不逐行复刻其内部实现（该类型未出现在反编译集中）。
        /// </summary>
        public static List<JavaInfo> Scan()
        {
            var result = new List<JavaInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string> probeHome = home =>
            {
                if (string.IsNullOrEmpty(home)) return;
                foreach (var rel in new[] { "bin\\java.exe", "jre\\bin\\java.exe" })
                {
                    string exe = home.TrimEnd('\\') + "\\" + rel;
                    AddIfJava(result, seen, exe);
                }
            };

            // 1. Mojang 运行时（PCL 会独立标记，优先级高于普通 JDK）
            string runtimeRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\.minecraft\\runtime\\";
            try
            {
                if (Directory.Exists(runtimeRoot))
                    foreach (var sub in Directory.EnumerateDirectories(runtimeRoot))
                        AddIfJava(result, seen, sub.TrimEnd('\\') + "\\bin\\java.exe", true);
            }
            catch { }

            // 2. 常见安装根目录下的一层子目录
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Java",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Eclipse Adoptium",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\AdoptOpenJDK",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Microsoft",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Zulu",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\Java",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\.jdks",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\scoop\\apps",
            };
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var sub in Directory.EnumerateDirectories(root))
                    {
                        probeHome(sub);
                        // jdk-21.0.10 下若还有一层（如 Microsoft\jdk-17.0.8.7-hotspot）
                        try
                        {
                            foreach (var sub2 in Directory.EnumerateDirectories(sub)) probeHome(sub2);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // 3. JAVA_HOME 与 PATH
            probeHome(Environment.GetEnvironmentVariable("JAVA_HOME"));
            string path = Environment.GetEnvironmentVariable("Path") ?? "";
            foreach (var dir in path.Split(';'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                AddIfJava(result, seen, dir.TrimEnd('\\') + "\\java.exe");
            }

            // PCL 的列表顺序来自目录枚举，实际选择是「第一个满足范围的」。
            // 离线实现改为「满足范围的最低版本优先」：新 Java 对旧 Forge 的兼容性差，
            // 例如 1.20.1 同时装了 17/21/25/26 时应选 17 而不是 26。
            // 同版本内 Mojang 运行时优先（路径更稳定，且与 --mc 隔离）。
            result.Sort((a, b) =>
            {
                int c = (a.Version ?? new System.Version(0, 0)).CompareTo(b.Version ?? new System.Version(0, 0));
                if (c != 0) return c;
                return a.MojangRuntime == b.MojangRuntime ? 0 : (a.MojangRuntime ? -1 : 1);
            });
            return result;
        }

        static void AddIfJava(List<JavaInfo> list, HashSet<string> seen, string exe, bool mojang = false)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                string full = Path.GetFullPath(exe);
                if (!seen.Add(full)) return;
                var info = QueryInfo(full);
                if (info == null) return;
                info.MojangRuntime = mojang;
                list.Add(info);
            }
            catch { }
        }

        /// <summary>
        /// 执行 `java -XshowSettings:properties -version`，一次取出版本、file.encoding、native.encoding。
        /// 对应 PCLCS.Java.CheckAsync（Java.cs）：同一个进程同时校验 64 位并记录编码属性。
        /// 输出在 stderr；`-XshowSettings` 让 8 及以前的 Java 也能报出属性。
        /// </summary>
        public static JavaInfo QueryInfo(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "-XshowSettings:properties -version")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.Default,
                    StandardOutputEncoding = Encoding.Default
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return null; }
                    string all = err + "\n" + outp;
                    var m = Regex.Match(all,
                        "(?:java|openjdk|jdk) version \"([0-9]+)(?:\\.([0-9]+))?(?:\\.([0-9]+))?(?:_([0-9]+))?");
                    if (!m.Success) return null;
                    int a = int.Parse(m.Groups[1].Value);
                    int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                    int c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                    int d = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
                    // Java 8 及以前是 1.8.0_291 形态，统一成 8.0.291.0
                    if (a == 1) { a = b; b = c; c = d; d = 0; }
                    if (a <= 4 || a >= 100) return null;   // PCL 同样把这些视为探测失败

                    string fileEnc = GetProperty(all, "file.encoding");
                    return new JavaInfo
                    {
                        ExePath = exe,
                        Folder = Path.GetDirectoryName(exe),
                        Version = new System.Version(a, b, c, d),
                        FileEncodingName = fileEnc,
                        NativeEncodingName = GetProperty(all, "native.encoding") ?? fileEnc,
                    };
                }
            }
            catch { return null; }
        }

        static string GetProperty(string output, string name)
        {
            string prefix = name + " = ";
            foreach (var line in output.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith(prefix, StringComparison.Ordinal))
                    return t.Substring(prefix.Length).Trim();
            }
            return null;
        }

        /// <summary>只要版本的轻量封装。</summary>
        public static System.Version QueryVersion(string exe)
        {
            var info = QueryInfo(exe);
            return info == null ? null : info.Version;
        }

        public static JavaInfo Select(List<JavaInfo> list, VersionRange range)
        {
            foreach (var j in list)
                if (range == null || range.Contains(j.Version)) return j;
            return null;
        }

        // ---------------------------------------------------------------------
        //  Mojang 运行时下载（ModJava.cs:686-751 与 CreateJavaDownloadLoader）
        // ---------------------------------------------------------------------

        sealed class RuntimePackage
        {
            public string Component;
            public string Name;
            public System.Version Version;
            public string ManifestUrl;
        }

        static List<RuntimePackage> GetRuntimeList()
        {
            string text = Net.GetString(RuntimeManifest);
            var root = J.Parse(text) as Dictionary<string, object>;
            var win = J.O(root, "windows-x64");
            if (win == null) throw new Exception("Java 运行时清单缺少 windows-x64 分支");

            var list = new List<RuntimePackage>();
            foreach (var kv in win)
            {
                if (string.Equals(kv.Key, "minecraft-java-exe", StringComparison.Ordinal)) continue;
                var arr = kv.Value as List<object>;
                if (arr == null || arr.Count == 0) continue;
                var first = arr[0] as Dictionary<string, object>;
                if (first == null) continue;
                string name = J.S(first, "version", "name");
                if (string.IsNullOrEmpty(name)) continue;

                System.Version ver;
                if (name.IndexOf('.') >= 0)
                {
                    var parts = name.Split('.');
                    ver = new System.Version(
                        (int)Num(parts, 0),
                        (int)Num(parts, 1),
                        (int)Num(parts, 2),
                        (int)Num(parts, 3));
                }
                else if (name.StartsWith("8u51-", StringComparison.Ordinal))
                    ver = new System.Version(8, 0, 51, 0);
                else
                {
                    string head = name;
                    int cut = head.IndexOfAny(new[] { 'u', '.' });
                    if (cut >= 0) head = head.Substring(0, cut);
                    ver = new System.Version((int)Num(head), 0, 0, 0);
                }

                list.Add(new RuntimePackage
                {
                    Component = kv.Key,
                    Name = name,
                    Version = ver,
                    ManifestUrl = J.S(first, "manifest", "url")
                });
            }
            return list;
        }

        static double Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        static double Num(string[] parts, int i)
        {
            return i < parts.Length ? Num(parts[i]) : 0;
        }

        /// <summary>
        /// 下载并安装 Mojang 提供的 Java 运行时。逐文件直落，无解压步骤
        /// （CreateJavaDownloadLoader，ModJava.cs:961-1015）。
        /// </summary>
        public static JavaInfo InstallRuntime(VersionRange range, string recommendedComponent, string mcFolder)
        {
            Log.Step("获取 Mojang Java 运行时清单");
            var list = GetRuntimeList();
            RuntimePackage picked = null;
            if (!string.IsNullOrEmpty(recommendedComponent))
                picked = list.FirstOrDefault(p => p.Component == recommendedComponent);
            if (picked == null)
                picked = list.FirstOrDefault(p => range == null || range.Contains(p.Version));
            if (picked == null)
                throw new Exception("Mojang 的 Java 列表中没有满足 " + (range ?? VersionRange.All()) + " 的运行时");

            Log.Info("选择 Java " + picked.Name + "（" + picked.Component + "）");

            string manifest = Net.GetString(picked.ManifestUrl);
            var root = J.Parse(manifest) as Dictionary<string, object>;
            var files = J.O(root, "files");
            if (files == null) throw new Exception("Java 运行时清单没有 files 字段");

            string baseDir = Util.Slash(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                "\\.minecraft\\runtime\\" + picked.Component);

            var tasks = new List<DownloadTask>();
            int skipped = 0;
            foreach (var kv in files)
            {
                var raw = J.O(kv.Value, "downloads", "raw");
                if (raw == null) continue;
                string sha1 = J.S(raw, "sha1");
                if (sha1 != null && Blacklist.Contains(sha1)) { skipped++; continue; }
                string url = J.S(raw, "url");
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(new DownloadTask
                {
                    Url = url,
                    MirrorUrl = Net.Mirror(url),
                    Path = baseDir + kv.Key.Replace('/', '\\'),
                    Size = J.N(raw, 0, "size"),
                    Sha1 = sha1,
                    Label = kv.Key
                });
            }
            if (skipped > 0) Log.Debug("跳过 " + skipped + " 个黑名单文件（legal/*）");

            Log.Step("下载 Java 运行时（" + tasks.Count + " 个文件）");
            Net.DownloadAll(tasks, "Java 运行时");

            string exe = baseDir + "bin\\java.exe";
            if (!File.Exists(exe)) throw new Exception("Java 运行时安装后找不到 java.exe：" + exe);
            var info = QueryInfo(exe) ?? new JavaInfo
            {
                ExePath = exe,
                Folder = baseDir.TrimEnd('\\') + "\\bin",
                Version = picked.Version,
            };
            info.MojangRuntime = true;
            Log.Ok("Java 运行时安装完成：" + baseDir);
            return info;
        }
    }

    // =========================================================================
    //  启动 —— 参数组装、占位符替换、natives、进程
    //  对应 ModLaunch.McLaunchArgumentMain / McLaunchArgumentsJVM /
    //       McLaunchArgumentsGame / McLaunchArgumentsReplace / SplitJavaArguments /
    //       DeduplicateJavaArguments / McLaunchNatives / McLaunchRun
    // =========================================================================
    sealed class Launch
    {
        public McInstance Instance;
        public Options Opt;
        public JavaInfo Java;
        public string PlayerName;
        public string Uuid;
        public string AccessToken;
        /// <summary>平台相关的路径（PCL 用短路径，避免空格/中文进入命令行）。</summary>
        public static bool UseShortPaths = true;

        static string P(string path) { return UseShortPaths ? Util.ToShortPath(path) : path; }

        // ---------------------------------------------------------------------
        //  入口
        // ---------------------------------------------------------------------

        /// <summary>完成参数组装与 natives 解压，返回可直接启动的进程信息。</summary>
        public ProcessStartInfo Build(bool prepareOnly = false)
        {
            Log.Step("获取启动参数");
            var args = new List<string>();

            Log.Debug("版本：" + Instance.Version.DisplayName());
            Log.Debug("内存：" + RamMb() + " MB");
            Log.Debug("Java：" + Java);

            args.AddRange(ArgumentsJvm());
            args.Add(Instance.MainClass ?? throw new Exception("版本 JSON 缺少 mainClass"));
            args.AddRange(ArgumentsGame());

            var replacements = BuildReplacements(args);
            var final = new List<string>(args.Count);
            foreach (var raw in args)
            {
                string s = raw;
                foreach (var kv in replacements) s = s.Replace(kv.Key, kv.Value);
                // ModLaunch.cs:1817 —— 含 shell 特殊字符且未加引号的 token 必须包引号
                if (s.IndexOfAny(new[] { '&', '|', '<', '>', '^', ' ' }) >= 0 &&
                    !s.StartsWith("\"", StringComparison.Ordinal) &&
                    !s.EndsWith("\"", StringComparison.Ordinal))
                    s = "\"" + s.Replace("\"", "\\\"") + "\"";
                final.Add(s);
            }

            Log.Step("解压 Natives");
            ExtractNatives();

            Log.Ok("启动参数就绪（" + final.Count + " 项）");
            if (Log.Verbose) foreach (var a in final) Log.Debug("  " + a);

            var psi = new ProcessStartInfo(Java.ExePath)
            {
                // UseShellExecute=false 时 Arguments 是单个字符串，按 Windows 命令行规则拼接。
                // 这里的 token 已经带引号，直接空格连接即可（与 PCL 的 McLaunchArgument 一致）。
                Arguments = string.Join(" ", final.ToArray()),
                WorkingDirectory = Instance.PathIndie,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = LaunchEncoding(),
                StandardErrorEncoding = LaunchEncoding()
            };

            // Path 追加 Java 所在目录（去重）
            string path = psi.EnvironmentVariables["Path"] ?? "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(path)) parts.AddRange(path.Split(';'));
            parts.Add(P(Java.Folder));
            psi.EnvironmentVariables["Path"] = string.Join(";", parts.Distinct().ToArray());
            // 关键：把 appdata 指向 MC 文件夹，隔离游戏对真实 APPDATA 的读写
            psi.EnvironmentVariables["appdata"] = P(Cfg.McFolder);

            return psi;
        }

        public Process Start(ProcessStartInfo psi = null)
        {
            if (psi == null) psi = Build();
            Log.Step("启动游戏进程");
            Log.Info(Java.ExePath);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Log.Info("[MC] " + e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Error("[MC] " + e.Data); };
            if (!p.Start()) throw new Exception("无法启动进程：" + Java.ExePath);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                p.PriorityBoostEnabled = true;
                if (Opt.Priority == 0) p.PriorityClass = ProcessPriorityClass.AboveNormal;
                else if (Opt.Priority == 2) p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { }

            Log.Ok("游戏已启动（PID " + p.Id + "）");
            return p;
        }

        // ---------------------------------------------------------------------
        //  内存与 GC
        // ---------------------------------------------------------------------

        /// <summary>
        /// 分配内存（MB）。对应 PageSetupLaunch.GetRam（PageSetupLaunch.xaml.cs:1253-1318）。
        /// PCL 由 Settings 的 LaunchRamType 决定：0 按可用物理内存自适应，1 走 0-40 滑块换算。
        /// 本实现把 CLI --ram 直接当 MB 用（等价于用户已选好值），未给时按类型 0 自适应。
        /// </summary>
        public int RamMb()
        {
            if (Opt.RamMb > 0) return Opt.RamMb;
            long avail = AvailablePhysicalBytes();
            double gb = Math.Round(avail / 1024.0 / 1024.0 / 1024.0 * 10.0) / 10.0;
            var ver = Instance.Version;

            // 对应 Instance.Modable：有 Fabric / Forge 系 / LiteLoader 才按 mods 数扩展
            bool modable = ver.HasFabric || ver.HasForgelike || ver.HasLiteLoader;
            double min, low, mid, high;
            if (modable)
            {
                int mods = CountMods(Instance.PathIndie);
                min = 0.5 + mods / 150.0;
                low = 1.5 + mods / 90.0;
                mid = 2.7 + mods / 50.0;
                high = 4.5 + mods / 25.0;
            }
            else if (ver.HasOptiFine)
            {
                min = 0.5; low = 1.5; mid = 3.0; high = 5.0;
            }
            else
            {
                min = 0.5; low = 1.5; mid = 2.5; high = 4.0;
            }

            double total = 0, rest = gb, seg;
            seg = low; total += Math.Min(rest, seg); rest -= seg;
            if (rest >= 0.1)
            {
                seg = mid - low; total += Math.Min(rest * 0.7, seg); rest -= seg / 0.7;
                if (rest >= 0.1)
                {
                    seg = high - mid; total += Math.Min(rest * 0.4, seg); rest -= seg / 0.4;
                    if (rest >= 0.1) { seg = high; total += Math.Min(rest * 0.15, seg); }
                }
            }
            total = Math.Round(Math.Max(total, min), 1);
            return (int)Math.Floor(total * 1024.0);
        }

        static int CountMods(string indie)
        {
            try
            {
                string dir = indie + "mods\\";
                if (!Directory.Exists(dir)) return 0;
                int n = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".jar" || ext == ".zip" || ext == ".litemod") n++;
                }
                return n;
            }
            catch { return 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        static long AvailablePhysicalBytes()
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(m)) return (long)m.ullAvailPhys;
            }
            catch { }
            return 4L * 1024 * 1024 * 1024;
        }

        // ---------------------------------------------------------------------
        //  JVM 参数（ModLaunch.cs:1828-2078）
        // ---------------------------------------------------------------------

        List<string> ArgumentsJvm()
        {
            var list = new List<string>();
            var jvmArgs = J.A(Instance.JsonObject, "arguments", "jvm");

            if (jvmArgs != null)
            {
                // 新版：沿继承链逐层收集，父层在前（PCL 从子向父遍历，依次加入）
                var chain = Instance.Chain();
                foreach (var inst in chain)
                {
                    var arr = J.A(inst.JsonObject, "arguments", "jvm");
                    if (arr == null) continue;
                    Log.Debug("新版 JVM 参数：从版本 JSON 获取（" + inst.Name + "）");
                    foreach (var node in arr)
                    {
                        var s = node as string;
                        if (s != null) { list.Add(s.Trim()); continue; }
                        var o = node as Dictionary<string, object>;
                        if (o == null) continue;
                        if (o.ContainsKey("rules") && !McInstance.McJsonRuleCheck(o["rules"])) continue;
                        object value;
                        if (!o.TryGetValue("value", out value)) continue;
                        var vs = value as string;
                        if (vs != null) { list.Add(vs.Trim()); continue; }
                        var vl = value as List<object>;
                        if (vl == null) continue;
                        foreach (var v in vl) { var x = v as string; if (x != null) list.Add(x.Trim()); }
                    }
                }
                // 含空格且未加引号的参数要包引号（ModLaunch.cs:1884）
                for (int i = 0; i < list.Count; i++)
                {
                    string a = list[i];
                    if (a.Contains(" ") && !a.StartsWith("\"", StringComparison.Ordinal) &&
                        !a.EndsWith("\"", StringComparison.Ordinal))
                        list[i] = "\"" + a.Replace("\"", "\\\"") + "\"";
                }
            }
            else
            {
                Log.Debug("旧版 JVM 参数");
                list.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe.minecraft.exe.heapdump");
                list.Add("-Djava.library.path=${natives_directory}");
                list.Add("-cp");
                list.Add("${classpath}");
            }

            // 用户自定义 JVM 参数
            list = Deduplicate(Split(string.Join(" ", list.ToArray()) +
                (string.IsNullOrWhiteSpace(Opt.JvmArgs) ? "" : " " + Opt.JvmArgs)), true);

            // JLW：PCL 在 GBK 环境下会禁用；本机 chcp=936，且 JavaWrapper.jar 属启动器随附资源，
            // 不在单文件交付范围内，因此这里不注入 -jar JavaWrapper.jar。
            Log.Debug("JLW：已禁用（GBK 环境 / 单文件交付不含 JavaWrapper.jar）");

            // -Xmx
            list.Add("-Xmx" + RamMb() + "m");

            // GC（ModLaunch.cs:1958-2029）
            int gc = Opt.GcMode < 0 ? 4 : Opt.GcMode;   // PCL 默认 LaunchAdvanceGC = 4
            if (gc != 3)
            {
                bool useG1;
                if ((gc == 0 && Java.Version.Major < 15) ||
                    (gc == 1 && Java.Version.Major < 21) || gc == 2 || gc == 4)
                    useG1 = true;
                else useG1 = false;
                if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 17763)
                    useG1 = true;

                Log.Debug("GC 设置：" + gc + "，选取 " + (useG1 ? "G1GC" : "ZGC") + "，Java " + Java.Version.Major);
                list.RemoveAll(a => Regex.IsMatch(a,
                    "( )*-XX:[+-]?(Use\\w+GC|ZGenerational|UseCompactObjectHeaders|G1\\w+Percent|G1\\w+Size|(Max|Min)(GCPauseMillis|HeapFreeRatio))"));
                list.Add("-XX:+UnlockExperimentalVMOptions");
                if (Java.Version.Major >= 24) list.Add("-XX:+UseCompactObjectHeaders");

                if (useG1 && gc == 4)
                {
                    list.Add("-XX:+UseG1GC");
                    list.Add("-XX:G1NewSizePercent=20");
                    list.Add("-XX:G1ReservePercent=20");
                    list.Add("-XX:G1HeapRegionSize=32M");
                    list.Add("-XX:MaxGCPauseMillis=50");
                    list.Add("-XX:+PerfDisableSharedMem");
                    if (Java.Version.Major <= 7) list.Add("-XX:MaxPermSize=512m");
                    if (Java.Version.Major == 8) list.Add("-XX:+ParallelRefProcEnabled");
                    if (Java.Version.Major >= 12) list.Add("-XX:MinHeapFreeRatio=25");
                    if (Java.Version.Major >= 12) list.Add("-XX:MaxHeapFreeRatio=40");
                }
                else if (useG1)
                {
                    list.Add("-XX:+UseG1GC");
                    list.Add("-XX:G1NewSizePercent=20");
                    list.Add("-XX:G1ReservePercent=20");
                    list.Add("-XX:G1HeapRegionSize=32M");
                    list.Add("-XX:MaxGCPauseMillis=50");
                    if (Java.Version.Major <= 7) list.Add("-XX:MaxPermSize=512m");
                }
                else
                {
                    list.Add("-XX:+UseZGC");
                    if (Java.Version.Major == 21 || Java.Version.Major == 22)
                        list.Add("-XX:+ZGenerational");
                }
            }

            list.Remove("-XX:MaxDirectMemorySize=256M");
            list.Add("-Dlog4j2.formatMsgNoLookups=true");

            // 编码（ModLaunch.cs:2032-2070）
            // 探测值来自 Java 自身的 file.encoding / native.encoding（见 Java.QueryInfo）：
            // Java 18 之前用 FileEncodingName，18 及之后用 NativeEncodingName；21+ 一律 UTF-8。
            int major = Java.Version.Major;
            Encoding enc = LaunchEncoding();
            string argName = enc.CodePage == 54936 ? "GBK" : enc.WebName;

            string stdoutKey = major < 19 ? "sun.stdout.encoding" : "stdout.encoding";
            string stderrKey = major < 19 ? "sun.stderr.encoding" : "stderr.encoding";
            if (!list.Any(a => a.StartsWith("-D" + stdoutKey + "=", StringComparison.Ordinal)))
                list.Add("-D" + stdoutKey + "=" + argName);
            if (!list.Any(a => a.StartsWith("-D" + stderrKey + "=", StringComparison.Ordinal)))
                list.Add("-D" + stderrKey + "=" + argName);
            if (major >= 18 && major < 21) list.Add("-Dfile.encoding=COMPAT");

            return Deduplicate(list, true);
        }

        /// <summary>
        /// 进程输出编码（ModLaunch.McLaunchEncoding，ModLaunch.cs:2032-2055）：
        /// Java 21+ 固定 UTF-8；否则用 Java 探测到的 native.encoding（18 前用 file.encoding）。
        /// 探测值缺失或无法识别时退回系统 ANSI 代码页。
        /// </summary>
        Encoding LaunchEncoding()
        {
            int major = Java.Version.Major;
            if (major >= 21) return new UTF8Encoding(false);

            string name = major < 18 ? Java.FileEncodingName : Java.NativeEncodingName;
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    return Encoding.GetEncoding(
                        string.Equals(name, "GBK", StringComparison.OrdinalIgnoreCase) ? "GB18030" : name);
                }
                catch (Exception ex)
                {
                    Log.Debug("无法识别 Java 编码 " + name + "，已回退为系统代码页：" + ex.Message);
                }
            }
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
            catch { return Encoding.Default; }
        }

        // ---------------------------------------------------------------------
        //  游戏参数（ModLaunch.cs:2080-2220）
        // ---------------------------------------------------------------------

        List<string> ArgumentsGame()
        {
            string acc = "";

            string legacy = J.S(Instance.JsonObject, "minecraftArguments");
            if (!string.IsNullOrEmpty(legacy))
            {
                Log.Debug("旧版游戏参数");
                acc += " " + legacy + " --height ${resolution_height} --width ${resolution_width}";
            }

            var gameArgs = J.A(Instance.JsonObject, "arguments", "game");
            if (gameArgs != null)
            {
                foreach (var inst in Instance.Chain())
                {
                    var arr = J.A(inst.JsonObject, "arguments", "game");
                    if (arr == null) continue;
                    Log.Debug("新版游戏参数：从版本 JSON 获取（" + inst.Name + "）");
                    foreach (var node in arr)
                    {
                        var s = node as string;
                        if (s != null) { acc += " " + QuoteIfNeeded(s); continue; }
                        var o = node as Dictionary<string, object>;
                        if (o == null) continue;
                        if (o.ContainsKey("rules") && !McInstance.McJsonRuleCheck(o["rules"])) continue;
                        object value;
                        if (!o.TryGetValue("value", out value)) continue;
                        var vs = value as string;
                        if (vs != null) { acc += " " + QuoteIfNeeded(vs); continue; }
                        var vl = value as List<object>;
                        if (vl == null) continue;
                        foreach (var v in vl) { var x = v as string; if (x != null) acc += " " + QuoteIfNeeded(x); }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(Opt.GameArgs)) acc += " " + Opt.GameArgs;

            // OptiFine + Forge/LiteLoader 的 TweakClass 纠正（ModLaunch.cs:2159-2182）
            var ver = Instance.Version;
            if ((ver.HasForge || ver.HasLiteLoader) && ver.HasOptiFine)
            {
                if (acc.Contains("--tweakClass optifine.OptiFineTweaker"))
                {
                    Log.Warn("发现错误的 OptiFine TweakClass，已纠正为 OptiFineForgeTweaker");
                    acc = acc.Replace(" --tweakClass optifine.OptiFineTweaker", "")
                             + " --tweakClass optifine.OptiFineForgeTweaker";
                }
            }

            var list = Deduplicate(Split(acc), false);
            if (Opt.Fullscreen) list.Add("--fullscreen");

            if (!string.IsNullOrEmpty(Opt.Server))
            {
                if (Instance.ReleaseTime > new DateTime(2023, 4, 4))
                {
                    list.Add("--quickPlayMultiplayer");
                    list.Add(Opt.Server);
                }
                else
                {
                    int idx = Opt.Server.IndexOf(':');
                    if (idx > 0)
                    {
                        list.Add("--server"); list.Add(Opt.Server.Substring(0, idx));
                        list.Add("--port"); list.Add(Opt.Server.Substring(idx + 1));
                    }
                    else
                    {
                        list.Add("--server"); list.Add(Opt.Server);
                        list.Add("--port"); list.Add("25565");
                    }
                }
            }
            return Deduplicate(list, false);
        }

        static string QuoteIfNeeded(string a)
        {
            if (a.Contains(" ") && !a.StartsWith("\"", StringComparison.Ordinal) &&
                !a.EndsWith("\"", StringComparison.Ordinal))
                return "\"" + a.Replace("\"", "\\\"") + "\"";
            return a;
        }

        // ---------------------------------------------------------------------
        //  占位符（ModLaunch.cs:2222-2334）
        // ---------------------------------------------------------------------

        List<KeyValuePair<string, string>> BuildReplacements(List<string> args)
        {
            var list = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (k, v) => list.Add(new KeyValuePair<string, string>(k, v ?? ""));

            add("${classpath_separator}", ";");
            add("${natives_directory}", Util.TrimSlash(NativesFolder()));
            add("${library_directory}", P(Cfg.McFolder + "libraries"));
            add("${libraries_directory}", P(Cfg.McFolder + "libraries"));
            add("${pure_directory}", Util.TrimSlash(Cfg.PureDirectory));
            add("${launcher_name}", "PCL");
            add("${launcher_version}", "408");
            add("${version_name}", Instance.Name);
            add("${game_directory}", P(Util.TrimSlash(Instance.PathIndie)));
            add("${assets_root}", P(Cfg.McFolder + "assets"));
            add("${user_properties}", "{}");
            add("${auth_player_name}", PlayerName);
            add("${auth_uuid}", Uuid);
            add("${uuid}", Uuid);
            add("${auth_access_token}", AccessToken);
            add("${access_token}", AccessToken);
            add("${auth_session}", AccessToken);
            add("${user_type}", "msa");
            add("${primary_jar}", P(Instance.PathVersion + Instance.Name + ".jar"));
            add("${game_assets}", P(Cfg.McFolder + "assets\\virtual\\legacy"));
            add("${assets_index_name}", Instance.AssetsIndexName);

            // ${version_type} 为空时，连同 --versionType 及其值一起移除（ModLaunch.cs:2251-2263）
            if (string.IsNullOrEmpty(Opt.VersionType))
            {
                int idx = args.IndexOf("--versionType");
                if (idx >= 0)
                {
                    args.RemoveAt(idx);
                    if (idx < args.Count) args.RemoveAt(idx);
                }
                add("${version_type}", "\"\"");
            }
            else add("${version_type}", Opt.VersionType);

            // 窗口尺寸
            double w = Math.Max(100, Opt.Width), h = Math.Max(100, Opt.Height);
            if (Instance.Version.Vaild && Instance.Version.Vanilla.Major <= 12 &&
                Java.Version >= new System.Version(8, 0, 200) && Java.Version <= new System.Version(8, 0, 321) &&
                !Instance.Version.HasOptiFine && !Instance.Version.HasForge)
            {
                // 高 DPI 下老版本会把窗口乘 DPI 再放大，这里按 DPI 反向缩放
                double dpiScale = CurrentDpi() / 96.0;
                if (dpiScale > 1) { w /= dpiScale; h /= dpiScale; }
            }
            add("${resolution_width}", Math.Round(w).ToString(CultureInfo.InvariantCulture));
            add("${resolution_height}", Math.Round(h).ToString(CultureInfo.InvariantCulture));

            // classpath：全部非 natives 库 + 主 jar，OptiFine 插到倒数第二位
            var parts = new List<string>();
            string optifine = null;
            foreach (var e in Instance.LibListGet(true))
            {
                if (e.IsNatives) continue;
                if (e.Name == "optifine:OptiFine") { optifine = e.LocalPath; continue; }
                parts.Add(P(e.LocalPath));
            }
            if (optifine != null && parts.Count >= 2)
                parts.Insert(parts.Count - 2, P(optifine));
            else if (optifine != null)
                parts.Add(P(optifine));
            add("${classpath}", string.Join(";", parts.ToArray()));

            return list;
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        static double CurrentDpi()
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int dpi = GetDeviceCaps(dc, 88);   // LOGPIXELSX
                ReleaseDC(IntPtr.Zero, dc);
                return dpi > 0 ? dpi : 96;
            }
            catch { return 96; }
        }

        // ---------------------------------------------------------------------
        //  Natives（ModLaunch.cs:2439-2538）
        // ---------------------------------------------------------------------

        /// <summary>
        /// natives 目录（GetNativesFolder）：默认 {PathVersion}{Name}-natives\；
        /// 非 GBK 环境且路径含非 ASCII 时退到 %APPDATA%\.minecraft\bin\natives\，再退到 ProgramData。
        /// </summary>
        public string NativesFolder()
        {
            string def = Util.Slash(P(Util.Slash(Instance.PathVersion))) + Instance.Name + "-natives\\";
            if (!Util.IsGbkEnvironment && !Util.IsAsciiOnly(def))
            {
                string alt = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                             + "\\.minecraft\\bin\\natives\\";
                def = Util.IsAsciiOnly(alt)
                    ? alt
                    : Path.GetPathRoot(Environment.SystemDirectory) + "ProgramData\\PCL\\natives\\";
            }
            return def;
        }

        /// <summary>解压所有 IsNatives 库内的 .dll，并清理目录中多余文件。</summary>
        void ExtractNatives()
        {
            string folder = NativesFolder();
            Util.EnsureDir(folder);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in Instance.LibListGet(true))
            {
                if (!e.IsNatives) continue;
                if (!File.Exists(e.LocalPath))
                {
                    Log.Warn("缺少 Natives 库：" + e.LocalPath);
                    continue;
                }
                try
                {
                    using (var zip = ZipFile.OpenRead(e.LocalPath))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                            // PCL: nativesFolder + entry.FullName（保留 zip 内相对路径）
                            string target = folder + entry.FullName.Replace('/', '\\');
                            expected.Add(target);
                            var fi = new FileInfo(target);
                            if (fi.Exists && fi.Length == entry.Length) continue;
                            Util.EnsureDir(Path.GetDirectoryName(target));
                            try { if (fi.Exists) fi.Delete(); }
                            catch (UnauthorizedAccessException)
                            {
                                Log.Warn("删除旧 dll 被拒绝（可能有游戏正在运行），跳过：" + target);
                                continue;
                            }
                            entry.ExtractToFile(target, true);
                            Log.Debug("已解压：" + target);
                        }
                    }
                }
                catch (InvalidDataException)
                {
                    Util.TryDelete(e.LocalPath);
                    throw new Exception("Natives 文件已损坏：" + e.LocalPath);
                }
            }

            // 删除目录内不在本次清单中的文件（McLaunchNatives 尾部）
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (expected.Contains(f)) continue;
                    Log.Debug("删除多余文件：" + f);
                    Util.TryDelete(f);
                }
            }
            catch (UnauthorizedAccessException) { Log.Debug("清理 natives 目录被拒绝，已跳过"); }
        }

        // ---------------------------------------------------------------------
        //  参数拆分与去重（ModLaunch.cs:2336-2437）
        // ---------------------------------------------------------------------

        /// <summary>SplitJavaArguments：按空格切分，双引号内的空格保留，\" 视为转义。</summary>
        public static List<string> Split(string arg)
        {
            var result = new List<string>();
            if (arg == null) return result;
            arg = arg.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            if (arg.Length == 0) return result;

            var sb = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\' && i + 1 < arg.Length && arg[i + 1] == '"')
                {
                    sb.Append("\\\"");
                    i++;
                    continue;
                }
                if (c == '"') { inQuote = !inQuote; sb.Append(c); continue; }
                if (c == ' ' && !inQuote)
                {
                    if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        /// <summary>
        /// DeduplicateJavaArguments：单值参数按整体去重；「-key value」成对参数中
        /// 游戏参数后者覆盖前者（--tweakClass 除外），JVM 参数相同才去重。
        /// </summary>
        public static List<string> Deduplicate(List<string> args, bool isJvm)
        {
            var list = new List<string>();
            int i = 0;
            while (i < args.Count)
            {
                string a = args[i];
                bool single = !a.StartsWith("-", StringComparison.Ordinal) || i + 1 >= args.Count ||
                              (args[i + 1].StartsWith("-", StringComparison.Ordinal) && ModVal(args[i + 1]) == 0.0);
                if (single)
                {
                    i++;
                    if (!list.Contains(a)) list.Add(a);
                    continue;
                }

                string v = args[i + 1];
                i += 2;
                int end = list.Count - 2;   // 只在已成对的参数里查找
                int k = 0;
                bool merged = false;
                while (k <= end)
                {
                    if (string.Equals(list[k], a, StringComparison.Ordinal))
                    {
                        if (!isJvm && !string.Equals(a, "--tweakClass", StringComparison.Ordinal))
                        {
                            Log.Debug("覆盖重复参数：" + a + " " + list[k + 1] + " → " + a + " " + v);
                            list[k + 1] = v;
                            merged = true;
                            break;
                        }
                        if (string.Equals(list[k + 1], v, StringComparison.Ordinal))
                        {
                            merged = true;
                            break;
                        }
                    }
                    k++;
                }
                if (!merged) { list.Add(a); list.Add(v); }
            }
            return list;
        }

        /// <summary>ModBase.Val：把 "-1.5" 之类的字符串转成数值，失败返回 0。</summary>
        static double ModVal(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string t = s.TrimStart('-');
            double d;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }
    }

    // =========================================================================
    //  离线登录 —— 对应 McLoginLegacyUuid / McLoginLegacyUuidWithCustomSkin
    // =========================================================================
    static class Login
    {
        /// <summary>
        /// 离线 UUID。对应 ModLaunch.McLoginLegacyUuid（ModLaunch.cs:1775-1779），
        /// 哈希用 MeloongCore.Extensions.StringExtensions.GetStableHashCode
        /// （MeloongCore 内嵌于 PCL 资源，已提取确认）：
        ///   h = 5381; h = (h &lt;&lt; 5) ^ h ^ c;  h ^= 0xA98F501BC684032F
        /// 拼接后再按 UUID v3 的位布局改写第 13 与 17 位。
        /// </summary>
        public static string OfflineUuid(string name)
        {
            name = name ?? "";
            ulong h = 5381;
            foreach (char c in name) h = (h << 5) ^ h ^ c;
            h ^= 0xA98F501BC684032FUL;

            string text = EnsureLength(name.Length.ToString("X"), '0', 16) +
                          EnsureLength(h.ToString("X"), '0', 16);
            return text.Substring(0, 12) + "3" + text.Substring(13, 3) + "9" + text.Substring(17, 15);
        }

        static string EnsureLength(string s, char c, int length)
        {
            if (string.IsNullOrEmpty(s)) s = "";
            return s.Length <= length ? s.PadLeft(length, c) : s.Substring(0, length);
        }

        /// <summary>离线 AccessToken：PCL 用 UUID 本身（ModLaunch.cs:1130），部分服务端要求非空字符串。</summary>
        public static string OfflineToken(string uuid) { return uuid; }
    }

    // =========================================================================
    //  命令行入口
    // =========================================================================
    static class Program
    {
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            var opt = new Options();
            opt.Command = "";

            try
            {
                if (!ParseArgs(args, opt)) return 0;

                // 双击运行（无命令行参数）时进入交互菜单，而不是打印帮助后闪退
                if (opt.Command.Length == 0 && args.Length == 0) opt.Command = "menu";

                Cfg.PureDirectory = Util.TrimSlash(Path.GetDirectoryName(
                    System.Reflection.Assembly.GetEntryAssembly().Location));
                Cfg.McFolder = Util.Slash(string.IsNullOrEmpty(opt.McFolder)
                    ? Path.Combine(Cfg.PureDirectory, ".minecraft")
                    : opt.McFolder);
                opt.McFolder = Cfg.McFolder;   // McInstance 从 Options 读，必须回写为规范形式
                opt.PureDirectory = Cfg.PureDirectory;

                Log.Verbose = opt.Verbose;
                if (opt.LogFile) Log.FilePath = Path.Combine(Cfg.PureDirectory, Brand.LogFile);
                if (opt.NoMirror) Net.PreferMirror = false;
                if (opt.Threads > 0) Net.Threads = opt.Threads;

                switch (opt.Command)
                {
                    case "list": return CmdList(opt);
                    case "install": return CmdInstall(opt);
                    case "java": return CmdJava(opt);
                    case "launch": return CmdLaunch(opt);
                    case "fix": return CmdFix(opt);
                    case "menu": return CmdMenu(opt);
                    case "version":
                        Console.WriteLine(Brand.Title);
                        Console.WriteLine("基于 Plain Craft Launcher 2 v2.13.1.1 (build 408) 的功能级逆向实现");
                        return 0;
                    default: PrintHelp(); return 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                if (Log.Verbose) Log.Error(ex.ToString());
                return 1;
            }
        }

        // ---------------------------------------------------------------------
        //  参数解析
        // ---------------------------------------------------------------------

        static bool ParseArgs(string[] args, Options o)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help": o.Command = "help"; return true;
                    case "-v":
                    case "--version": o.Command = "version"; return true;
                    case "--verbose": o.Verbose = true; Log.Verbose = true; break;
                    case "--log": o.LogFile = true; break;
                    case "--no-mirror": o.NoMirror = true; break;
                    case "--no-assets": o.NoAssets = true; break;
                    case "--verify": o.Verify = true; break;
                    case "--no-java-download": o.NoJavaDownload = true; break;
                    case "--fullscreen": o.Fullscreen = true; break;
                    case "--no-short-path": Launch.UseShortPaths = false; break;
                    case "--list": o.Command = "list"; break;
                    case "--install":
                        o.Command = "install";
                        o.Target = Next(args, ref i, "--install");
                        break;
                    case "--fix":
                        o.Command = "fix";
                        o.Target = Next(args, ref i, "--fix");
                        break;
                    case "--launch":
                        o.Command = "launch";
                        o.Target = Next(args, ref i, "--launch");
                        break;
                    case "--java":
                        // 不带参数时是「检测 Java」，带实例名时是「检查该实例的 Java 解析」
                        o.Command = "java";
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            o.Target = args[++i];
                        break;
                    case "--mc": o.McFolder = Next(args, ref i, "--mc"); break;
                    case "--user": o.UserName = Next(args, ref i, "--user"); break;
                    case "--ram": o.RamMb = ParseInt(Next(args, ref i, "--ram"), 0); break;
                    case "--gc": o.GcMode = ParseInt(Next(args, ref i, "--gc"), -1); break;
                    case "--server": o.Server = Next(args, ref i, "--server"); break;
                    case "--threads": o.Threads = ParseInt(Next(args, ref i, "--threads"), 0); break;
                    case "--width": o.Width = ParseInt(Next(args, ref i, "--width"), 854); break;
                    case "--height": o.Height = ParseInt(Next(args, ref i, "--height"), 480); break;
                    case "--jvm": o.JvmArgs = Next(args, ref i, "--jvm"); break;
                    case "--game-args": o.GameArgs = Next(args, ref i, "--game-args"); break;
                    case "--version-type": o.VersionType = Next(args, ref i, "--version-type"); break;
                    case "--indie": o.Indie = true; break;
                    case "--no-indie": o.Indie = false; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--wait": o.WaitExit = true; break;
                    case "--offline": o.Offline = true; break;
                    case "--install-only": o.InstallOnly = true; break;
                    case "--launch-after": o.LaunchAfterInstall = true; break;
                    default:
                        if (a.Length > 0 && a[0] != '-' && string.IsNullOrEmpty(o.Target))
                        {
                            // 允许 `PCL-跳跳鱼小队专用版 愚者` 这样的简写：等价于 --launch
                            o.Target = a;
                            if (string.IsNullOrEmpty(o.Command)) o.Command = "launch";
                        }
                        else Log.Warn("忽略未知参数：" + a);
                        break;
                }
            }

            if (o.Command == "help" || o.Command == "version") return true;
            if (string.IsNullOrEmpty(o.Command)) return true;
            if ((o.Command == "launch" || o.Command == "install" || o.Command == "fix") &&
                string.IsNullOrEmpty(o.Target))
                throw new Exception("--" + o.Command + " 需要指定版本名");
            return true;
        }

        static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new Exception(flag + " 缺少参数值");
            return args[++i];
        }

        static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s, out v) ? v : def;
        }

        // ---------------------------------------------------------------------
        //  子命令
        // ---------------------------------------------------------------------

        static void PrintBanner()
        {
            Log.Info(Brand.Title);
            Log.Info("MC 目录：" + Cfg.McFolder);
        }

        static int CmdList(Options opt)
        {
            string dir = Cfg.McFolder + "versions\\";
            if (!Directory.Exists(dir)) { Log.Warn("没有 versions 目录：" + dir); return 0; }

            Log.Step("已安装的版本");
            int n = 0;
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(sub);
                string json = sub + "\\" + name + ".json";
                if (!File.Exists(json)) continue;
                n++;
                try
                {
                    var inst = new McInstance(name, opt);
                    var v = inst.Version;
                    string extra = inst.InheritName.Length > 0 ? "（继承 " + inst.InheritName + "）" : "";
                    Log.Info("  " + name + "  →  " + v.DisplayName() + extra);
                }
                catch (Exception ex)
                {
                    Log.Info("  " + name + "  →  <无法读取：" + ex.Message + ">");
                }
            }
            Log.Ok("共 " + n + " 个版本");
            return 0;
        }

        static int CmdInstall(Options opt)
        {
            PrintBanner();
            string name = Downloader.InstallVanilla(opt.Target, null, !opt.NoAssets);
            if (opt.InstallOnly) return 0;

            if (opt.LaunchAfterInstall)
            {
                opt.Target = name;
                return CmdLaunch(opt);
            }
            return 0;
        }

        static int CmdFix(Options opt)
        {
            PrintBanner();
            var inst = new McInstance(opt.Target, opt);
            Log.Step("补全实例文件：" + inst.Name);
            Downloader.FixInstance(inst, !opt.NoAssets, opt.Verify);
            Log.Ok("补全完成");
            return 0;
        }

        static int CmdJava(Options opt)
        {
            PrintBanner();
            Log.Step("扫描本机 Java");
            var list = Java.Scan();
            if (list.Count == 0) Log.Warn("未找到任何可用的 Java");
            foreach (var j in list)
                Log.Info("  " + (j.MojangRuntime ? "[Mojang] " : "") + j.Version + "  " + j.ExePath);

            if (string.IsNullOrEmpty(opt.Target)) { Log.Ok("共 " + list.Count + " 个 Java"); return 0; }

            var inst = new McInstance(opt.Target, opt);
            string comp;
            var range = Java.GetRequirement(inst, out comp);
            Log.Step("实例 " + inst.Name + " 的 Java 要求");
            Log.Info("版本：" + inst.Version.DisplayName());
            Log.Info("要求的范围：" + range + (string.IsNullOrEmpty(comp) ? "" : "，推荐组件 " + comp));
            var picked = Java.Select(list, range);
            if (picked == null) Log.Warn("本机没有满足要求的 Java；启动时会尝试从 Mojang 下载");
            else Log.Ok("将使用：" + picked.Version + "  " + picked.ExePath);
            return 0;
        }

        static int CmdLaunch(Options opt)
        {
            PrintBanner();
            var inst = new McInstance(opt.Target, opt);

            // 1. 补全文件（DlClientFix 的等价物，默认按大小判断，--verify 时校验 SHA-1）
            Log.Step("检查游戏文件");
            Downloader.FixInstance(inst, !opt.NoAssets, opt.Verify);

            // 2. Java
            string comp;
            var range = Java.GetRequirement(inst, out comp);
            Log.Step("选择 Java");
            Log.Debug("要求：" + range + (string.IsNullOrEmpty(comp) ? "" : "，推荐组件 " + comp));
            var javaList = Java.Scan();
            var java = Java.Select(javaList, range);
            if (java == null)
            {
                if (opt.NoJavaDownload)
                    throw new Exception("没有满足 " + range + " 的 Java，且已禁用自动下载");
                Log.Warn("本机没有满足要求的 Java，尝试从 Mojang 下载");
                java = Java.InstallRuntime(range, comp, Cfg.McFolder);
            }
            Log.Ok("使用 " + java);

            // 3. 组装并启动
            var launcher = new Launch
            {
                Instance = inst,
                Opt = opt,
                Java = java,
                PlayerName = opt.UserName,
            };
            launcher.Uuid = Login.OfflineUuid(opt.UserName);
            launcher.AccessToken = Login.OfflineToken(launcher.Uuid);

            var psi = launcher.Build();
            if (opt.DryRun)
            {
                Log.Step("仅打印启动命令（--dry-run）");
                Log.Info(psi.FileName + " " + psi.Arguments);
                return 0;
            }

            var proc = launcher.Start(psi);
            if (opt.WaitExit)
            {
                proc.WaitForExit();
                Log.Info("游戏已退出，返回码 " + proc.ExitCode);
                return proc.ExitCode;
            }
            return 0;
        }

        // ---------------------------------------------------------------------
        //  交互菜单（双击运行时的入口）
        // ---------------------------------------------------------------------

        static int CmdMenu(Options opt)
        {
            // 记住最近一次的选择，避免每次都要重输
            string lastVersion = LoadSetting("LastVersion");
            string lastUser = LoadSetting("UserName");
            if (!string.IsNullOrEmpty(lastUser)) opt.UserName = lastUser;

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("  ╔══════════════════════════════════════════════════════╗");
                Console.WriteLine("  ║   " + Brand.Title);
                Console.WriteLine("  ╚══════════════════════════════════════════════════════╝");
                Console.WriteLine("  MC 目录：" + Cfg.McFolder);

                var versions = ListVersions(opt);
                if (versions.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("  （还没有安装任何版本）");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("  已安装的版本：");
                    for (int i = 0; i < versions.Count; i++)
                    {
                        string mark = versions[i].Name == lastVersion ? "  ← 上次启动" : "";
                        Console.WriteLine("    [" + (i + 1) + "] " + versions[i].Name +
                                          "   " + versions[i].Desc + mark);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("  [数字] 启动对应版本");
                Console.WriteLine("  [l]    重新列出    [j] 查看本机 Java");
                Console.WriteLine("  [i]    下载并安装新版本（如 1.20.1）");
                Console.WriteLine("  [u]    修改用户名（当前：" + opt.UserName + "）");
                Console.WriteLine("  [s]    修改 MC 目录");
                Console.WriteLine("  [q]    退出");
                Console.Write("  请选择 > ");

                string input = (Console.ReadLine() ?? "").Trim();
                if (input.Length == 0) continue;
                if (input == "q" || input == "Q") return 0;

                try
                {
                    int idx;
                    if (int.TryParse(input, out idx))
                    {
                        if (idx < 1 || idx > versions.Count)
                        {
                            Log.Warn("没有这个编号");
                            Pause();
                            continue;
                        }
                        string target = versions[idx - 1].Name;
                        opt.Target = target;
                        SaveSetting("LastVersion", target);
                        SaveSetting("UserName", opt.UserName);
                        Console.WriteLine();
                        int code = CmdLaunch(opt);
                        Console.WriteLine();
                        Log.Ok("游戏已退出（返回码 " + code + "）");
                        Pause();
                        continue;
                    }

                    switch (input.ToLowerInvariant())
                    {
                        case "l":
                            continue;   // 回到循环顶部即重新枚举

                        case "j":
                            Console.WriteLine();
                            CmdJava(new Options
                            {
                                McFolder = opt.McFolder,
                                PureDirectory = opt.PureDirectory,
                                Command = "java",
                                Target = "",
                                UserName = opt.UserName,
                            });
                            Pause();
                            continue;

                        case "i":
                            Console.Write("  要安装的版本号（如 1.20.1）> ");
                            string ver = (Console.ReadLine() ?? "").Trim();
                            if (ver.Length == 0) continue;
                            Console.WriteLine();
                            opt.Target = ver;
                            CmdInstall(opt);
                            Pause();
                            continue;

                        case "u":
                            Console.Write("  新的用户名 > ");
                            string name = (Console.ReadLine() ?? "").Trim();
                            if (name.Length == 0) continue;
                            opt.UserName = name;
                            SaveSetting("UserName", name);
                            Log.Ok("用户名已改为 " + name);
                            continue;

                        case "s":
                            Console.Write("  新的 .minecraft 目录 > ");
                            string dir = (Console.ReadLine() ?? "").Trim().Trim('"');
                            if (dir.Length == 0) continue;
                            Cfg.McFolder = Util.Slash(dir);
                            opt.McFolder = Cfg.McFolder;
                            SaveSetting("McFolder", dir);
                            continue;

                        default:
                            Log.Warn("无法识别的输入：" + input);
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Message);
                    if (Log.Verbose) Log.Error(ex.ToString());
                    Pause();
                }
            }
        }

        sealed class VersionItem
        {
            public string Name;
            public string Desc;
        }

        static List<VersionItem> ListVersions(Options opt)
        {
            var result = new List<VersionItem>();
            string dir = Cfg.McFolder + "versions\\";
            if (!Directory.Exists(dir)) return result;
            foreach (var sub in Directory.EnumerateDirectories(dir)
                                           .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(sub);
                if (!File.Exists(sub + "\\" + name + ".json")) continue;
                string desc = "";
                try { desc = new McInstance(name, opt).Version.DisplayName(); }
                catch { desc = "<无法读取>"; }
                result.Add(new VersionItem { Name = name, Desc = desc });
            }
            return result;
        }

        static void Pause()
        {
            Console.WriteLine();
            Console.Write("  按回车继续…");
            Console.ReadLine();
        }

        static string SettingsPath
        {
            get { return Path.Combine(Cfg.PureDirectory, Brand.SettingsFile); }
        }

        static string LoadSetting(string key)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    int i = line.IndexOf(':');
                    if (i <= 0) continue;
                    if (line.Substring(0, i).Trim() == key) return line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        static void SaveSetting(string key, string value)
        {
            try
            {
                var lines = new List<string>();
                if (File.Exists(SettingsPath))
                {
                    foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                    {
                        int i = line.IndexOf(':');
                        if (i > 0 && line.Substring(0, i).Trim() == key) continue;
                        if (line.Trim().Length > 0) lines.Add(line);
                    }
                }
                lines.Add(key + ":" + value);
                File.WriteAllLines(SettingsPath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"PCL-跳跳鱼小队专用版 —— 最小 Minecraft 启动器（下载 / 装 Java / 启动）

用法：
  PCL-跳跳鱼小队专用版                  双击或直接运行：进入交互菜单
  PCL-跳跳鱼小队专用版 --list            列出已安装版本
  PCL-跳跳鱼小队专用版 --install <版本>   下载并安装原版（如 1.20.1）
  PCL-跳跳鱼小队专用版 --fix <实例>       补全已有实例的缺失文件
  PCL-跳跳鱼小队专用版 --java            扫描本机 Java
  PCL-跳跳鱼小队专用版 --java <实例>      查看该实例的 Java 要求与选择结果
  PCL-跳跳鱼小队专用版 --launch <实例>    启动（会先补全文件、选择 Java）
  PCL-跳跳鱼小队专用版 <实例>             等价于 --launch <实例>

常用选项：
  --mc <目录>           .minecraft 目录（默认：启动器目录\.minecraft）
  --user <名字>         离线登录用户名（默认 Player）
  --ram <MB>            最大堆内存（默认按物理内存与 mods 数自动估算）
  --gc <0-4>            GC 策略：0 旧 Java 自动 / 1 新 Java 自动 / 2 强制 G1 / 3 不设置 / 4 G1 增强（默认 4）
  --server <地址>       启动后自动进服
  --fullscreen          全屏
  --width/--height      窗口尺寸（默认 854×480）
  --jvm ""...""          追加 JVM 参数
  --game-args ""...""    追加游戏参数
  --no-assets           跳过资源文件下载
  --verify              按 SHA-1 校验已有文件（较慢）
  --no-java-download    禁止自动下载 Java
  --no-mirror           不使用 BMCLAPI 镜像
  --threads <N>         下载线程数（默认 8）
  --no-short-path       不把路径转成 8.3 短路径
  --dry-run             只打印启动命令，不真正启动
  --wait                等待游戏退出并返回其退出码
  --verbose             输出调试信息
  --log                 同时写入日志文件
");
        }
    }
}
