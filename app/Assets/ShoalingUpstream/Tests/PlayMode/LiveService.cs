using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace ShoalingUpstream.Tests.Integration
{
    /// <summary>
    /// A real `node service/src/server.mjs`, started for the duration of a test run.
    ///
    /// Its own port and its own copy of the journey data, never the artist's. Two reasons, and
    /// both of them are the reason these tests exist rather than more mocks:
    ///
    /// * A device that joins the live bus becomes visible in the operator's window and its status
    ///   reports overwrite the bus's authoritative state. A test that borrows the running service
    ///   would move the numbers on somebody's screen.
    ///
    /// * The published revision in `data/` is the superseded seeded layout, and publishing the
    ///   current draft over it is the artist's decision, not a test's. Copying the whole journey
    ///   directory to a temp dir and publishing THERE gets the current artwork in front of the app
    ///   without touching a byte the artist owns — revisions stay immutable where it matters.
    /// </summary>
    public sealed class LiveService : IDisposable
    {
        private readonly Process _process;
        private readonly string _journeyDir;
        private readonly StringBuilder _log = new();

        public int Port { get; }
        public string HttpUrl => $"http://127.0.0.1:{Port}";
        public string DeviceWsUrl => $"ws://127.0.0.1:{Port}/ws?role=device";
        public string OperatorWsUrl => $"ws://127.0.0.1:{Port}/ws?role=operator";

        /// <summary>Where the scratch copy of `data/journeys` lives, for a test that wants to
        /// publish into it.</summary>
        public string JourneyDir => _journeyDir;

        public string Log => _log.ToString();

        /// <summary>The repository root — the app's project folder is `app/` inside it.</summary>
        public static string RepoRoot =>
            Directory.GetParent(UnityEngine.Application.dataPath)!.Parent!.FullName;

        private LiveService(Process process, int port, string journeyDir)
        {
            _process = process;
            Port = port;
            _journeyDir = journeyDir;
        }

        public static LiveService Start()
        {
            string root = RepoRoot;
            string server = ServerEntryPoint(root);

            string journeyDir = Path.Combine(
                Path.GetTempPath(), "shoaling-playmode-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            CopyTree(Path.Combine(root, "data", "journeys"), journeyDir);

            int port = FreePort();

            var info = new ProcessStartInfo(NodePath(), $"\"{server}\"")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.EnvironmentVariables["PORT"] = port.ToString();
            info.EnvironmentVariables["HOST"] = "127.0.0.1";
            info.EnvironmentVariables["JOURNEY_DIR"] = journeyDir;

            var process = Process.Start(info);
            if (process is null) throw new InvalidOperationException("could not start node");

            var service = new LiveService(process, port, journeyDir);
            process.OutputDataReceived += (_, e) => service.Note(e.Data);
            process.ErrorDataReceived += (_, e) => service.Note(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            service.WaitUntilHealthy();
            return service;
        }

        private void Note(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_log) _log.AppendLine(line);
        }

        /// <summary>Block until /api/health answers. Blocking rather than a coroutine because
        /// nothing in these tests renders, and a fixture that is half-started is worse than one
        /// that takes a second.</summary>
        private void WaitUntilHealthy(int timeoutMs = 20000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"the service exited with {_process.ExitCode} before answering:\n{Log}");
                }
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create($"{HttpUrl}/api/health");
                    request.Timeout = 1000;
                    using var response = (HttpWebResponse)request.GetResponse();
                    if (response.StatusCode == HttpStatusCode.OK) return;
                }
                catch (WebException) { /* not up yet */ }
                Thread.Sleep(100);
            }
            throw new TimeoutException($"the service never became healthy:\n{Log}");
        }

        /// <summary>A plain HTTP call against the scratch service, for publishing and for reading
        /// back what the app is about to be handed.</summary>
        public string Http(string method, string path, string body = null)
        {
            var request = (HttpWebRequest)WebRequest.Create(HttpUrl + path);
            request.Method = method;
            request.Timeout = 10000;
            if (body != null)
            {
                request.ContentType = "application/json";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using var stream = request.GetRequestStream();
                stream.Write(bytes, 0, bytes.Length);
            }
            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream()!);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited) _process.Kill();
                _process.WaitForExit(3000);
            }
            catch (Exception e) { Debug.LogWarning($"[LiveService] could not stop node: {e.Message}"); }

            try { if (Directory.Exists(_journeyDir)) Directory.Delete(_journeyDir, true); }
            catch (Exception e) { Debug.LogWarning($"[LiveService] temp dir left behind: {e.Message}"); }

            _process.Dispose();
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// The service's entry point, whatever it is written in this week.
        ///
        /// It was `server.mjs` when these tests were written and became `server.ts` while they
        /// were running — Node has run TypeScript unflagged since 22.18, so the change costs a
        /// caller nothing but the filename. Looking for both means this fixture does not break
        /// the next time somebody elsewhere in the repo is mid-conversion, and the error when
        /// none exists says what was actually looked for.
        /// </summary>
        private static string ServerEntryPoint(string root)
        {
            string dir = Path.Combine(root, "service", "src");
            foreach (string name in new[] { "server.ts", "server.mjs", "server.js" })
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(
                $"no service entry point in {dir} — looked for server.ts, server.mjs, server.js");
        }

        /// <summary>Ask the OS for a port, then hand the number to node. A window exists between
        /// releasing it and node binding it; on a loopback interface with nothing else claiming
        /// ports it has never mattered, and a collision fails loudly in WaitUntilHealthy rather
        /// than quietly connecting somewhere else.</summary>
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>Unity's batchmode PATH is not a login shell's, so node is looked for where it
        /// actually is before falling back to asking a shell.</summary>
        private static string NodePath()
        {
            string configured = Environment.GetEnvironmentVariable("SHOALING_NODE");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             ".local", "bin", "node"),
                "/opt/homebrew/bin/node",
                "/usr/local/bin/node",
                "/usr/bin/node",
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            try
            {
                var shell = Process.Start(new ProcessStartInfo("/bin/bash", "-lc \"command -v node\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                string found = shell!.StandardOutput.ReadToEnd().Trim();
                shell.WaitForExit(5000);
                if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
            }
            catch (Exception) { /* fall through to the error below */ }

            throw new FileNotFoundException(
                "no node executable found — set SHOALING_NODE to its full path");
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(from, to));
            }
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(from, to), true);
            }
        }
    }
}
