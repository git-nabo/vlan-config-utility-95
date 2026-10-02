using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VlanConfig95.Core
{
    /// <summary>Outcome of a single PowerShell invocation, including diagnostics.</summary>
    public sealed class PowerShellResult
    {
        public bool Success;
        public string Output;
        public string Error;
        public string Command;
        public int ExitCode;

        public PowerShellResult()
        {
            Output = string.Empty;
            Error = string.Empty;
            Command = string.Empty;
            ExitCode = -1;
        }
    }

    /// <summary>
    /// Runs Windows PowerShell (5.1, in-box on Windows 10/11) out-of-process.
    /// We deliberately avoid referencing System.Management.Automation so the utility
    /// stays a single small EXE with no extra assemblies.
    /// </summary>
    public static class PowerShellRunner
    {
        private const int DefaultTimeoutMs = 45000;

        public const string PayloadStart = "###VLANCFG:";
        public const string PayloadEnd = ":END###";

        /// <summary>True when a usable powershell.exe can be located.</summary>
        public static bool IsAvailable(out string versionInfo)
        {
            versionInfo = null;
            try
            {
                string exe = ResolveExecutable();
                if (exe == null)
                    return false;

                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exe);
                versionInfo = "Windows PowerShell " + fvi.FileVersion;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Escapes a value as a single-quoted PowerShell string literal. Handles the
        /// embedded single quotes that appear in adapter names such as "Alice's NIC".
        /// </summary>
        public static string Quote(string value)
        {
            if (value == null)
                return "''";

            return "'" + value.Replace("'", "''") + "'";
        }

        /// <summary>Encodes a script as Base64 UTF-16LE for -EncodedCommand.</summary>
        public static string EncodeCommand(string script)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        public static PowerShellResult Run(string script)
        {
            return Run(script, DefaultTimeoutMs);
        }

        public static PowerShellResult Run(string script, int timeoutMs)
        {
            PowerShellResult result = new PowerShellResult();
            result.Command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand <script>";

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ResolveExecutable() ?? "powershell.exe";
            psi.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                            EncodeCommand(script);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            object outLock = new object();
            object errLock = new object();

            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo = psi;

                    // Read both streams asynchronously so a full pipe buffer cannot
                    // deadlock the child process.
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null)
                            return;
                        lock (outLock)
                            stdout.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null)
                            return;
                        lock (errLock)
                            stderr.AppendLine(e.Data);
                    };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* best effort */ }
                        result.Success = false;
                        result.ExitCode = -2;
                        result.Error = "The PowerShell command timed out after " +
                                       (timeoutMs / 1000) + " seconds.";
                        return result;
                    }

                    p.WaitForExit();
                    result.ExitCode = p.ExitCode;
                }

                lock (outLock)
                    result.Output = stdout.ToString().Trim();
                lock (errLock)
                    result.Error = stderr.ToString().Trim();

                result.Success = (result.ExitCode == 0);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                result.Success = false;
                result.Error = "PowerShell could not be started: " + ex.Message;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Extracts the text between the payload sentinel emitted by our scripts,
        /// or null when the sentinel is absent.
        /// </summary>
        public static string ExtractPayload(PowerShellResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.Output))
                return null;

            int start = result.Output.IndexOf(PayloadStart, StringComparison.Ordinal);
            if (start < 0)
                return null;
            start += PayloadStart.Length;

            int end = result.Output.IndexOf(PayloadEnd, start, StringComparison.Ordinal);
            if (end < 0)
                return null;

            return result.Output.Substring(start, end - start);
        }

        private static string ResolveExecutable()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

            string candidate = Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(sys, @"SysWOW64\WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(candidate))
                return candidate;

            return null;
        }
    }
}