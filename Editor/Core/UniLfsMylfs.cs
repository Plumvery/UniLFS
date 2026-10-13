using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Runs mylfs (https://github.com/Plumvery/mylfs), the Node.js CLI that
    /// does every UniLFS operation: hashing, the manifest, staging, the ignore
    /// blocks, the sync baseline and all storage traffic. This package is the
    /// Unity front end to it - windows, prompts, Auto Sync, the asset menu and
    /// the .meta guard - so the two can never disagree about what a state
    /// means, and a project can be operated from a terminal or CI with the
    /// same tool and no Unity license.
    ///
    /// mylfs ships inside the package (Tools~/mylfs, invisible to Unity's
    /// importer) and needs Node.js 18 or later. Both can be overridden per
    /// machine: the Node.js / mylfs fields in Project Settings > UniLFS, or
    /// the UNILFS_NODE / UNILFS_MYLFS environment variables.
    ///
    /// Every call goes through mylfs's machine interface: one JSON result on
    /// stdout, one JSON progress event per line on stderr, and a "cancel" line
    /// on stdin for a graceful stop that still saves what finished.
    /// </summary>
    public static class UniLfsMylfs
    {
        public const string EnvNode = "UNILFS_NODE";
        public const string EnvMylfs = "UNILFS_MYLFS";
        public const string PackageName = "com.plumvery.unilfs";

        /// <summary>The oldest mylfs whose machine interface this package speaks.</summary>
        public static readonly Version MinimumMylfsVersion = new Version(0, 2, 0);
        const int MinimumNodeMajor = 18;

        /// <summary>How long a cancelled run gets to save its work before it is killed.</summary>
        const int CancelGraceMs = 15000;

        static string _packageRoot;
        static string _checkedNode;
        static string _checkedMylfs;

        [InitializeOnLoadMethod]
        static void Init()
        {
            // PackageInfo is a main-thread API; operations run on worker
            // threads, so the answer is taken here once.
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UniLfsMylfs).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                    _packageRoot = UniLfsPaths.Normalize(info.resolvedPath);
            }
            catch (Exception)
            {
                // Found by the fallbacks in PackageRoot instead.
            }
        }

        /// <summary>The installed UniLFS package directory, or null when it cannot be found.</summary>
        public static string PackageRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(_packageRoot) && Directory.Exists(_packageRoot)) return _packageRoot;
                string root = UniLfsPaths.ProjectRoot;
                string embedded = UniLfsPaths.Combine(root, "Packages/" + PackageName);
                if (Directory.Exists(embedded)) return embedded;
                string cache = UniLfsPaths.Combine(root, "Library/PackageCache");
                try
                {
                    if (Directory.Exists(cache))
                    {
                        var hit = Directory.GetDirectories(cache, PackageName + "*").FirstOrDefault();
                        if (hit != null) return UniLfsPaths.Normalize(hit);
                    }
                }
                catch (Exception) { }
                return null;
            }
        }

        // ------------------------------------------------------------ locating

        /// <summary>
        /// The mylfs entry script: the per-machine override, then
        /// UNILFS_MYLFS, then the copy bundled with this package.
        /// </summary>
        public static string ResolveMylfs(UniLfsUserSettings user = null)
        {
            if (user == null) user = UniLfsUserSettings.Load();
            foreach (var candidate in new[] { user.mylfsPath, Environment.GetEnvironmentVariable(EnvMylfs) })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                string path = candidate.Trim();
                // A checkout or an npm install directory works as well as the script itself.
                if (Directory.Exists(path)) path = Path.Combine(path, "bin", "mylfs.js");
                if (File.Exists(path)) return UniLfsPaths.Normalize(Path.GetFullPath(path));
                throw new UniLfsConfigException("mylfs was not found at '" + candidate + "'. Fix the mylfs path in Project Settings > UniLFS"
                    + " (or " + EnvMylfs + "), or clear it to use the copy bundled with UniLFS.");
            }
            string package = PackageRoot;
            if (package != null)
            {
                string bundled = UniLfsPaths.Combine(package, "Tools~/mylfs/bin/mylfs.js");
                if (File.Exists(bundled)) return bundled;
            }
            throw new UniLfsConfigException("The mylfs copy bundled with UniLFS is missing (expected Tools~/mylfs inside the "
                + PackageName + " package). Reinstall the package, or point Project Settings > UniLFS > mylfs at a mylfs checkout.");
        }

        /// <summary>
        /// The Node.js executable: the per-machine override, UNILFS_NODE, a
        /// copy dropped into the package's Tools~/node (what CI does), PATH,
        /// and then the places installers put it - an editor started from the
        /// Dock or a desktop shortcut does not see the PATH a terminal has.
        /// </summary>
        public static string ResolveNode(UniLfsUserSettings user = null)
        {
            if (user == null) user = UniLfsUserSettings.Load();
            foreach (var candidate in new[] { user.nodePath, Environment.GetEnvironmentVariable(EnvNode) })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (File.Exists(candidate.Trim())) return candidate.Trim();
                throw new UniLfsConfigException("Node.js was not found at '" + candidate + "'. Fix the Node.js path in Project Settings > UniLFS (or "
                    + EnvNode + "), or clear it to search the usual install locations.");
            }
            foreach (var candidate in NodeCandidates())
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            throw new UniLfsConfigException("UniLFS needs Node.js " + MinimumNodeMajor + " or later, and none was found. Install it from"
                + " https://nodejs.org, or set its path in Project Settings > UniLFS > Node.js.");
        }

        static IEnumerable<string> NodeCandidates()
        {
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            string exe = windows ? "node.exe" : "node";
            string package = PackageRoot;
            if (package != null)
            {
                yield return Path.Combine(package, "Tools~", "node", exe);
                yield return Path.Combine(package, "Tools~", "node", "bin", exe);
            }
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(dir)) yield return SafeCombine(dir.Trim('"'), exe);

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (windows)
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
                if (!string.IsNullOrEmpty(nvmSymlink)) yield return SafeCombine(nvmSymlink, exe);
                if (!string.IsNullOrEmpty(programFiles)) yield return SafeCombine(Path.Combine(programFiles, "nodejs"), exe);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    yield return SafeCombine(Path.Combine(localAppData, "Programs", "nodejs"), exe);
                    yield return SafeCombine(Path.Combine(localAppData, "Volta", "bin"), exe);
                }
                yield return @"C:\nvm4w\nodejs\node.exe";
                if (!string.IsNullOrEmpty(home)) yield return SafeCombine(Path.Combine(home, "scoop", "shims"), exe);
            }
            else
            {
                yield return "/opt/homebrew/bin/node";
                yield return "/usr/local/bin/node";
                yield return "/usr/bin/node";
                yield return "/opt/local/bin/node";
                if (!string.IsNullOrEmpty(home))
                {
                    yield return Path.Combine(home, ".volta", "bin", "node");
                    yield return Path.Combine(home, ".asdf", "shims", "node");
                    yield return Path.Combine(home, ".local", "share", "fnm", "aliases", "default", "bin", "node");
                    yield return Path.Combine(home, ".fnm", "aliases", "default", "bin", "node");
                    // nvm keeps one directory per version and no stable link;
                    // take the newest.
                    string nvm = Path.Combine(home, ".nvm", "versions", "node");
                    string[] versions = new string[0];
                    try { if (Directory.Exists(nvm)) versions = Directory.GetDirectories(nvm); } catch (Exception) { }
                    foreach (var dir in versions.OrderByDescending(d => ParseVersion(Path.GetFileName(d))))
                        yield return Path.Combine(dir, "bin", "node");
                }
            }
        }

        static string SafeCombine(string dir, string file)
        {
            try { return Path.Combine(dir, file); }
            catch (Exception) { return null; }
        }

        static Version ParseVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) return new Version(0, 0);
            var digits = new string(text.TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            var parts = digits.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            int major = 0, minor = 0, patch = 0;
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            if (parts.Length > 2) int.TryParse(parts[2], out patch);
            return new Version(major, minor, patch);
        }

        /// <summary>
        /// Checks the Node.js and mylfs this machine would use, once per
        /// combination per editor session, and returns a one-line description.
        /// Throws <see cref="UniLfsConfigException"/> when either is unusable.
        /// </summary>
        public static string Check(UniLfsUserSettings user = null)
        {
            if (user == null) user = UniLfsUserSettings.Load();
            string node = ResolveNode(user);
            string mylfs = ResolveMylfs(user);
            string nodeVersion = RunPlain(node, "--version");
            if (ParseVersion(nodeVersion).Major < MinimumNodeMajor)
                throw new UniLfsConfigException("UniLFS needs Node.js " + MinimumNodeMajor + " or later; " + node + " is " + nodeVersion.Trim()
                    + ". Update it, or point Project Settings > UniLFS > Node.js at a newer one.");
            string mylfsVersion = RunPlain(node, Quote(mylfs) + " --version");
            if (ParseVersion(mylfsVersion) < MinimumMylfsVersion)
                throw new UniLfsConfigException("UniLFS needs mylfs " + MinimumMylfsVersion + " or later; " + mylfs + " is " + mylfsVersion.Trim() + ".");
            _checkedNode = node;
            _checkedMylfs = mylfs;
            return "Node.js " + nodeVersion.Trim() + " (" + node + "), mylfs " + mylfsVersion.Trim() + " (" + mylfs + ")";
        }

        static string RunPlain(string exe, string arguments)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var stderr = p.StandardError.ReadToEndAsync();
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        throw new UniLfsConfigException("'" + exe + " " + arguments + "' did not answer within 15 seconds.");
                    }
                    if (p.ExitCode != 0)
                        throw new UniLfsConfigException("'" + exe + " " + arguments + "' failed: " + stderr.Result.Trim());
                    return stdout;
                }
            }
            catch (UniLfsConfigException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new UniLfsConfigException("Could not run Node.js at '" + exe + "': " + e.Message);
            }
        }

        // ------------------------------------------------------------- running

        /// <summary>
        /// Runs one mylfs command against the current project and returns its
        /// JSON result. Errors mylfs reports come back as the matching
        /// exception: <see cref="UniLfsBusyException"/>,
        /// <see cref="UniLfsConfigException"/>, <see cref="OperationCanceledException"/>
        /// or <see cref="UniLfsStorageException"/>.
        /// </summary>
        /// <param name="paths">Passed through a temporary file, so any number of them fits on the command line.</param>
        internal static async Task<string> RunAsync(string command, IEnumerable<string> flags, IEnumerable<string> paths,
            IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var user = UniLfsUserSettings.Load();
            string node = ResolveNode(user);
            string mylfs = ResolveMylfs(user);
            if (node != _checkedNode || mylfs != _checkedMylfs) Check(user);

            string root = UniLfsPaths.ProjectRoot;
            var args = new List<string> { Quote(mylfs), "--root", Quote(root), "--json", "--cancel-on-stdin" };
            if (progress != null) args.Add("--progress-json");
            args.Add(command);
            if (flags != null) args.AddRange(flags.Select(Quote));

            string pathsFile = null;
            if (paths != null)
            {
                pathsFile = Path.Combine(Path.GetTempPath(), "unilfs-paths-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(pathsFile, string.Join("\n", paths) + "\n", new UTF8Encoding(false));
                args.Add("--paths-file");
                args.Add(Quote(pathsFile));
            }

            var psi = new ProcessStartInfo(node, string.Join(" ", args.ToArray()))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = root,
            };

            var reporter = progress != null ? new UniLfsProgressReporter(progress) : null;
            var events = new ProgressEvents(reporter);
            var stderrTail = new StringBuilder();
            Process process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception e)
            {
                if (pathsFile != null) TryDelete(pathsFile);
                throw new UniLfsConfigException("Could not start Node.js at '" + node + "': " + e.Message);
            }

            try
            {
                using (process)
                using (ct.Register(() => Cancel(process)))
                {
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = Task.Run(async () =>
                    {
                        string line;
                        while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            if (line.StartsWith("{", StringComparison.Ordinal) && events.Handle(line)) continue;
                            lock (stderrTail)
                            {
                                stderrTail.AppendLine(line);
                                if (stderrTail.Length > 8000) stderrTail.Remove(0, stderrTail.Length - 8000);
                            }
                        }
                    });
                    string stdout = await stdoutTask.ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    if (reporter != null) reporter.Finish();

                    string json = LastJsonLine(stdout);
                    if (json == null)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        string detail;
                        lock (stderrTail) detail = stderrTail.ToString().Trim();
                        throw new UniLfsStorageException("mylfs " + command + " exited with code " + process.ExitCode + " and no result."
                            + (detail.Length > 0 ? "\n" + detail : ""));
                    }
                    ThrowIfError(json, ct);
                    return json;
                }
            }
            finally
            {
                if (pathsFile != null) TryDelete(pathsFile);
            }
        }

        /// <summary>Blocking form for the few synchronous callers (Untrack, Keep Mine).</summary>
        internal static string Run(string command, IEnumerable<string> flags, IEnumerable<string> paths)
        {
            return RunAsync(command, flags, paths, null, CancellationToken.None).GetAwaiter().GetResult();
        }

        static void Cancel(Process process)
        {
            try
            {
                if (process.HasExited) return;
                process.StandardInput.WriteLine("cancel");
                process.StandardInput.Flush();
            }
            catch (Exception) { }
            // Grace period for mylfs to save what finished, then stop waiting.
            Task.Delay(CancelGraceMs).ContinueWith(_ =>
            {
                try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
            });
        }

        static string LastJsonLine(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            var lines = stdout.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.StartsWith("{", StringComparison.Ordinal)) return l;
            }
            return null;
        }

        [Serializable]
        class ErrorBody
        {
            public string kind;
            public string message;
        }

        [Serializable]
        class ErrorEnvelope
        {
            public ErrorBody error;
        }

        static void ThrowIfError(string json, CancellationToken ct)
        {
            ErrorEnvelope envelope = null;
            try { envelope = JsonUtility.FromJson<ErrorEnvelope>(json); } catch (Exception) { }
            // JsonUtility fills an absent nested object with defaults rather
            // than null, so "no error" is an empty kind.
            if (envelope == null || envelope.error == null || string.IsNullOrEmpty(envelope.error.kind)) return;
            string message = envelope.error.message ?? "";
            switch (envelope.error.kind)
            {
                case "busy":
                    throw new UniLfsBusyException("another process (" + message + ")");
                case "config":
                    throw new UniLfsConfigException(message);
                case "cancelled":
                    throw new OperationCanceledException(message, ct);
                default:
                    throw new UniLfsStorageException(message);
            }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }

        /// <summary>
        /// Quotes one argument for ProcessStartInfo.Arguments. Windows rules
        /// (backslashes are literal unless they precede a quote), which Mono's
        /// argument splitter on macOS and Linux reads the same way for the
        /// paths this passes.
        /// </summary>
        internal static string Quote(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\'', '$', '`', '\\', '&', '(', ')', ';', '|', '<', '>' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------ progress

        [Serializable]
        class Event
        {
            public string type;
            public string phase;
            public int id;
            public string path;
            public long size;
            public long bytes;
            public int total;
            public long totalBytes;
            public float start;
            public float span;
            public string url;
        }

        /// <summary>Feeds mylfs's progress events into the package's own reporter.</summary>
        sealed class ProgressEvents
        {
            readonly UniLfsProgressReporter _reporter;
            readonly Dictionary<int, UniLfsTransfer> _items = new Dictionary<int, UniLfsTransfer>();

            public ProgressEvents(UniLfsProgressReporter reporter)
            {
                _reporter = reporter;
            }

            /// <summary>Returns false for a line that is not a progress event.</summary>
            public bool Handle(string line)
            {
                Event ev;
                try { ev = JsonUtility.FromJson<Event>(line); }
                catch (Exception) { return false; }
                if (ev == null || string.IsNullOrEmpty(ev.type)) return false;
                if (ev.type == "auth-url")
                {
                    UnityEngine.Debug.Log("UniLFS: if no browser opened, sign in to Google Drive here:\n" + ev.url);
                    return true;
                }
                if (_reporter == null) return true;
                lock (_items)
                {
                    switch (ev.type)
                    {
                        case "phase":
                            foreach (var open in _items.Values) open.Dispose();
                            _items.Clear();
                            _reporter.BeginPhase(ev.phase, ev.start, ev.span, ev.total, ev.totalBytes);
                            break;
                        case "start":
                            _items[ev.id] = _reporter.Begin(ev.path, ev.size);
                            break;
                        case "bytes":
                            UniLfsTransfer item;
                            if (_items.TryGetValue(ev.id, out item)) item.Report(ev.bytes);
                            break;
                        case "done":
                            UniLfsTransfer finished;
                            if (_items.TryGetValue(ev.id, out finished))
                            {
                                finished.Dispose();
                                _items.Remove(ev.id);
                            }
                            break;
                    }
                }
                return true;
            }
        }
    }
}
