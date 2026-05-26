using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EveOPreview.Services.Implementation
{
    sealed class ProcessMonitor : IProcessMonitor
    {
        #region Private constants
        private const string DEFAULT_PROCESS_NAME = "ExeFile";
        private const string CURRENT_PROCESS_NAME = "EVE-O Preview";
        private const int INITIAL_CACHE_CAPACITY = 64;
        private const string TEST_SERVER_TAG = "测试服";
        #endregion

        #region Private fields
        private readonly IDictionary<IntPtr, string> _processCache;
        private readonly IDictionary<IntPtr, string> _processServerNames;
        private IProcessInfo _currentProcessInfo;
        #endregion

        public ProcessMonitor()
        {
            this._processCache = new Dictionary<IntPtr, string>(INITIAL_CACHE_CAPACITY);
            this._processServerNames = new Dictionary<IntPtr, string>(INITIAL_CACHE_CAPACITY);

            // This field cannot be initialized properly in constructor
            // At the moment this code is executed the main application window is not yet initialized
            this._currentProcessInfo = new ProcessInfo(IntPtr.Zero, "");
        }

        private string GetServerName(Process process)
        {
            try
            {
                string fileName = process.MainModule.FileName;
                if (fileName.IndexOf(TEST_SERVER_TAG, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf("SISI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf("Singularity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf("test server", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return TEST_SERVER_TAG;
                }
            }
            catch
            {
                // Can't access process module info (e.g. access denied, process exited)
                // Assume official server
            }
            return null;
        }

        private bool IsMonitoredProcess(string processName)
        {
            // This is a possible extension point
            return String.Equals(processName, ProcessMonitor.DEFAULT_PROCESS_NAME, StringComparison.OrdinalIgnoreCase);
        }

        private IProcessInfo GetCurrentProcessInfo()
        {
            using (var currentProcess = Process.GetCurrentProcess())
            {
                return new ProcessInfo(currentProcess.MainWindowHandle, currentProcess.MainWindowTitle);
            }
        }

        public IProcessInfo GetMainProcess()
        {
            if (this._currentProcessInfo.Handle == IntPtr.Zero)
            {
                var processInfo = this.GetCurrentProcessInfo();

                // Are we initialized yet?
                if (processInfo.Title != "")
                {
                    this._currentProcessInfo = processInfo;
                }
            }

            return this._currentProcessInfo;
        }

        public ICollection<IProcessInfo> GetAllProcesses()
        {
            ICollection<IProcessInfo> result = new List<IProcessInfo>(this._processCache.Count);

            foreach (KeyValuePair<IntPtr, string> entry in this._processCache)
            {
                this._processServerNames.TryGetValue(entry.Key, out string serverName);
                result.Add(new ProcessInfo(entry.Key, entry.Value, serverName));
            }

            return result;
        }

        public void GetUpdatedProcesses(out ICollection<IProcessInfo> addedProcesses, out ICollection<IProcessInfo> updatedProcesses, out ICollection<IProcessInfo> removedProcesses)
        {
            addedProcesses = new List<IProcessInfo>(16);
            updatedProcesses = new List<IProcessInfo>(16);
            removedProcesses = new List<IProcessInfo>(16);

            // Use HashSet for O(1) removal instead of List's O(n) Remove
            HashSet<IntPtr> knownProcesses = new HashSet<IntPtr>(this._processCache.Keys);

            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    string processName;
                    IntPtr mainWindowHandle;
                    string mainWindowTitle;

                    try
                    {
                        processName = process.ProcessName;
                        mainWindowHandle = process.MainWindowHandle;
                        if (mainWindowHandle == IntPtr.Zero)
                        {
                            continue; // No need to monitor non-visual processes
                        }
                        mainWindowTitle = process.MainWindowTitle;
                    }
                    catch
                    {
                        continue; // Process may have exited between enumeration and property access
                    }

                    if (!this.IsMonitoredProcess(processName))
                    {
                        continue;
                    }

                    this._processCache.TryGetValue(mainWindowHandle, out string cachedTitle);

                    if (cachedTitle == null)
                    {
                        // This is a new process in the list — detect its server type
                        string serverName = GetServerName(process);
                        if (serverName != null)
                        {
                            this._processServerNames[mainWindowHandle] = serverName;
                        }

                        this._processCache.Add(mainWindowHandle, mainWindowTitle);
                        addedProcesses.Add(new ProcessInfo(mainWindowHandle, mainWindowTitle, serverName));
                    }
                    else
                    {
                        // This is an already known process
                        if (cachedTitle != mainWindowTitle)
                        {
                            this._processCache[mainWindowHandle] = mainWindowTitle;
                            this._processServerNames.TryGetValue(mainWindowHandle, out string serverName);
                            updatedProcesses.Add(new ProcessInfo(mainWindowHandle, mainWindowTitle, serverName));
                        }

                        knownProcesses.Remove(mainWindowHandle); // O(1) with HashSet
                    }
                }
            }
            finally
            {
                // Ensure Process objects are properly disposed
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }

            foreach (IntPtr index in knownProcesses)
            {
                string title = this._processCache[index];
                removedProcesses.Add(new ProcessInfo(index, title));
                this._processCache.Remove(index);
                this._processServerNames.Remove(index);
            }
        }
    }
}
