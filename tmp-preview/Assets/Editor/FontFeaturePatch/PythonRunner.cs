using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace ArabicStudy.Editor.FontFeaturePatch
{
    /// Thin wrapper around System.Diagnostics.Process for invoking a
    /// Python script and capturing its stdout. Stderr is captured
    /// separately so the caller can surface diagnostics to the Unity
    /// console without polluting the JSON stream on stdout.
    ///
    /// Finds Python in this priority:
    ///   1. <project>/.venv/Scripts/python.exe   (Windows venv)
    ///   2. <project>/.venv/bin/python           (Unix venv)
    ///   3. `python` on PATH
    ///   4. `python3` on PATH
    public static class PythonRunner
    {
        public struct RunResult
        {
            public bool launched;       // false if we couldn't find python
            public int exitCode;
            public string stdout;
            public string stderr;
            public string pythonPath;   // which interpreter we ended up using
            public string commandLine;  // for diagnostic logging
        }

        public static RunResult Run(string scriptPath, string[] scriptArgs,
            string workingDirectory = null, int timeoutMs = 60_000)
        {
            var result = new RunResult();
            var py = FindPython(workingDirectory ?? Directory.GetCurrentDirectory());
            if (py == null)
            {
                result.stderr = "could not locate a python interpreter (.venv or PATH)";
                return result;
            }

            var args = new StringBuilder();
            args.Append('"').Append(scriptPath).Append('"');
            if (scriptArgs != null)
            {
                foreach (var a in scriptArgs)
                {
                    args.Append(' ');
                    args.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = py,
                Arguments = args.ToString(),
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Avoid Windows console code page mangling Arabic glyph names
            // we get back via stderr diagnostics.
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            result.pythonPath = py;
            result.commandLine = $"\"{py}\" {args}";

            try
            {
                using var p = new Process { StartInfo = psi };
                var stdoutSb = new StringBuilder();
                var stderrSb = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
                p.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    result.stderr = $"python process timed out after {timeoutMs}ms\n" + stderrSb;
                    return result;
                }
                // Drain async readers.
                p.WaitForExit();
                result.launched = true;
                result.exitCode = p.ExitCode;
                result.stdout = stdoutSb.ToString();
                result.stderr = stderrSb.ToString();
            }
            catch (Exception ex)
            {
                result.stderr = $"{ex.GetType().Name}: {ex.Message}";
            }
            return result;
        }

        private static string FindPython(string projectRoot)
        {
            string[] candidates;
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                candidates = new[]
                {
                    Path.Combine(projectRoot, ".venv", "Scripts", "python.exe"),
                    "python.exe",
                    "python",
                    "python3",
                };
            }
            else
            {
                candidates = new[]
                {
                    Path.Combine(projectRoot, ".venv", "bin", "python"),
                    "python3",
                    "python",
                };
            }

            foreach (var c in candidates)
            {
                if (Path.IsPathRooted(c))
                {
                    if (File.Exists(c)) return c;
                }
                else if (ExistsOnPath(c))
                {
                    return c;
                }
            }
            return null;
        }

        private static bool ExistsOnPath(string program)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = program,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p.WaitForExit(2000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
